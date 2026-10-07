using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Conduit.Host.Core.Agents;

/// <summary>
/// Child process speaking newline-delimited JSON on stdin/stdout. Handles Windows npm shims (.cmd/.bat),
/// serialises writes, keeps the tail of stderr for diagnostics and kills the whole tree on dispose.
/// </summary>
public sealed class JsonlProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Channel<string> _writes = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly LinkedList<string> _stderrTail = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _writer;
    private Task? _reader;
    private Task? _stderr;

    public event Action<JsonObject>? Message;
    public event Action<int?>? Exited;

    public int ProcessId => _process.Id;

    private JsonlProcess(Process p) => _process = p;

    public static JsonlProcess Start(string executable, IEnumerable<string> args, string workingDirectory, IDictionary<string, string>? env = null)
    {
        var psi = CreateStartInfo(executable, args, workingDirectory);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.StandardInputEncoding = new UTF8Encoding(false);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var jp = new JsonlProcess(p);
        p.Exited += (_, _) =>
        {
            int? code = null;
            try { code = p.ExitCode; } catch (InvalidOperationException) { }
            // Let the reader drain stdout before reporting the exit.
            _ = Task.Run(async () =>
            {
                if (jp._reader is not null) await Task.WhenAny(jp._reader, Task.Delay(2000));
                jp.Exited?.Invoke(code);
            });
        };
        if (!p.Start()) throw new InvalidOperationException($"Failed to start {executable}");
        jp._reader = Task.Run(jp.ReadLoop);
        jp._stderr = Task.Run(jp.StderrLoop);
        jp._writer = Task.Run(jp.WriteLoop);
        return jp;
    }

    public static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> args, string workingDirectory)
    {
        var argList = args.ToList();
        ProcessStartInfo psi;
        var ext = Path.GetExtension(executable).ToLowerInvariant();
        if (OperatingSystem.IsWindows() && (ext == ".cmd" || ext == ".bat"))
        {
            // npm installs agents as .cmd shims; they must run through cmd.exe.
            var inner = string.Join(' ', new[] { Quote(executable) }.Concat(argList.Select(Quote)));
            psi = new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"{inner}\"" };
        }
        else
        {
            psi = new ProcessStartInfo(executable);
            foreach (var a in argList) psi.ArgumentList.Add(a);
        }
        psi.WorkingDirectory = workingDirectory;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        return psi;
    }

    private static string Quote(string a) =>
        a.Length > 0 && a.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^']) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    public void Send(JsonNode message) => _writes.Writer.TryWrite(message.ToJsonString());

    public string StderrTail()
    {
        lock (_stderrTail) return string.Join('\n', _stderrTail);
    }

    private async Task WriteLoop()
    {
        var stdin = _process.StandardInput;
        try
        {
            await foreach (var line in _writes.Reader.ReadAllAsync(_cts.Token))
            {
                await stdin.WriteAsync(line);
                await stdin.WriteAsync('\n');
                await stdin.FlushAsync();
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested || _process.HasExited) { }
        catch (IOException) { }
    }

    private async Task ReadLoop()
    {
        var stdout = _process.StandardOutput;
        try
        {
            while (await stdout.ReadLineAsync(_cts.Token) is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                JsonObject? obj;
                try { obj = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (obj is null) continue;
                try { Message?.Invoke(obj); }
                catch (Exception ex) { Debug.WriteLine($"adapter handler failed: {ex}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private async Task StderrLoop()
    {
        var stderr = _process.StandardError;
        try
        {
            while (await stderr.ReadLineAsync(_cts.Token) is { } line)
            {
                lock (_stderrTail)
                {
                    _stderrTail.AddLast(line);
                    while (_stderrTail.Count > 40) _stderrTail.RemoveFirst();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _writes.Writer.TryComplete();
        try
        {
            if (!_process.HasExited)
            {
                try { _process.StandardInput.Close(); } catch (Exception) { }
                using var wait = new CancellationTokenSource(1500);
                try { await _process.WaitForExitAsync(wait.Token); }
                catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); }
            }
        }
        catch (InvalidOperationException) { }
        _cts.Cancel();
        _process.Dispose();
    }
}
