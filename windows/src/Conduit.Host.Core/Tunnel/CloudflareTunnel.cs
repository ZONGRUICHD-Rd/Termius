using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Conduit.Host.Core.Agents;

namespace Conduit.Host.Core.Tunnel;

/// <summary>
/// Runs <c>cloudflared</c> so the phone can reach this PC from anywhere through Cloudflare's network
/// (outbound-only, no port forwarding, works behind CGNAT). WebSockets pass through Cloudflare unchanged and
/// every frame is still end-to-end encrypted between phone and host.
///
///  • quick: <c>cloudflared tunnel --url http://127.0.0.1:PORT</c> → random https://xxx.trycloudflare.com (changes on restart)
///  • token: <c>cloudflared tunnel run --token …</c> → your own hostname, configured in the Cloudflare dashboard
///           with the service http://localhost:PORT (stable; recommended).
/// cloudflared is taken from settings, PATH, or downloaded once from Cloudflare's GitHub releases.
/// </summary>
public sealed partial class CloudflareTunnel : IAsyncDisposable
{
    private readonly HostRuntime _host;
    private readonly CloudflareSettings _settings;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private Task? _loop;

    public string Status { get; private set; } = "starting";
    public string? Error { get; private set; }
    /// <summary>https://… public URL once known.</summary>
    public string? PublicUrl { get; private set; }
    public event Action? Changed;

    public CloudflareTunnel(HostRuntime host, CloudflareSettings settings, int port)
    {
        _host = host;
        _settings = settings;
        _port = port;
        if (settings.Mode == "token" && !string.IsNullOrWhiteSpace(settings.Hostname))
            PublicUrl = "https://" + settings.Hostname.Trim().Replace("https://", "").Replace("http://", "").TrimEnd('/');
    }

    /// <summary>wss:// form the phone connects to.</summary>
    public string? WebSocketUrl => PublicUrl?.Replace("https://", "wss://");

    public void Start() => _loop = Task.Run(Loop);

    private void Set(string status, string? error = null)
    {
        Status = status;
        Error = error;
        Changed?.Invoke();
    }

    private async Task Loop()
    {
        var backoff = 2000;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_settings.Mode == "token" && string.IsNullOrWhiteSpace(_settings.Token))
                {
                    Set("error", "请填写 Cloudflare Tunnel 令牌（Token）");
                    return;
                }
                Set("starting", "正在准备 cloudflared…");
                var exe = await EnsureBinaryAsync(_cts.Token);
                var args = _settings.Mode == "token"
                    ? new List<string> { "tunnel", "--no-autoupdate", "run", "--token", _settings.Token.Trim() }
                    : ["tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{_port}"];
                var psi = JsonlProcess.CreateStartInfo(exe, args, AppContext.BaseDirectory);
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                using var p = Process.Start(psi) ?? throw new InvalidOperationException("cloudflared 启动失败");
                _process = p;
                Set("starting", "正在连接 Cloudflare…");
                var readOut = ReadAsync(p.StandardOutput);
                var readErr = ReadAsync(p.StandardError);
                await p.WaitForExitAsync(_cts.Token);
                await Task.WhenAll(readOut, readErr);
                if (_cts.IsCancellationRequested) break;
                Set("error", $"cloudflared 已退出（代码 {p.ExitCode}），稍后重试");
                _host.Log($"cloudflared exited with {p.ExitCode}");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Set("error", ex.Message);
                _host.Log("Cloudflare Tunnel: " + ex.Message);
            }
            try { await Task.Delay(backoff, _cts.Token); } catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, 60_000);
        }
    }

    private async Task ReadAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (_settings.Mode != "token" && QuickUrlRegex().Match(line) is { Success: true } m && PublicUrl != m.Value)
            {
                PublicUrl = m.Value;
                _host.Log($"Cloudflare Tunnel URL: {PublicUrl}");
                Set("starting", "已获得地址，正在注册连接…");
            }
            if (line.Contains("Registered tunnel connection", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Connection registered", StringComparison.OrdinalIgnoreCase))
            {
                Set("online");
            }
            else if (line.Contains(" ERR ", StringComparison.Ordinal) && Status != "online")
            {
                Error = line.Length > 200 ? line[^200..] : line;
                Changed?.Invoke();
            }
        }
    }

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com")]
    private static partial Regex QuickUrlRegex();

    private async Task<string> EnsureBinaryAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_settings.CloudflaredPath) && File.Exists(_settings.CloudflaredPath)) return _settings.CloudflaredPath!;
        if (AgentCatalog.FindOnPath("cloudflared") is { } onPath) return onPath;

        var dir = Path.Combine(_host.DataDirectory, "bin");
        Directory.CreateDirectory(dir);
        var isWindows = OperatingSystem.IsWindows();
        var target = Path.Combine(dir, isWindows ? "cloudflared.exe" : "cloudflared");
        if (File.Exists(target)) return target;

        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
        var asset = isWindows ? $"cloudflared-windows-{arch}.exe" : $"cloudflared-linux-{arch}";
        var url = $"https://github.com/cloudflare/cloudflared/releases/latest/download/{asset}";
        Set("starting", "首次使用：正在下载 cloudflared…");
        _host.Log($"Downloading {url}");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var tmp = target + ".download";
        await using (var src = await http.GetStreamAsync(url, ct))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst, ct);
        File.Move(tmp, target, overwrite: true);
        if (!isWindows) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return target;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _process?.Kill(entireProcessTree: true); } catch (Exception) { }
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(2000));
    }
}
