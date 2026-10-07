using System.Diagnostics;

namespace Conduit.Host.Core.Agents;

/// <summary>Finds installed agents and creates adapters for them.</summary>
public sealed class AgentCatalog
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Pi = "pi";
    public const string Opencode = "opencode";
    public const string Echo = "demo";

    private sealed record Spec(string Id, string Name, string Vendor, string Command, string[] Candidates, Func<IAgentAdapter> Factory, string Detail);

    private readonly HostConfig _config;
    private readonly Dictionary<string, AgentInfo> _cache = [];
    private readonly object _lock = new();

    public AgentCatalog(HostConfig config) => _config = config;

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private static IEnumerable<Spec> Specs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Env("APPDATA");
        var local = Env("LOCALAPPDATA");
        yield return new Spec(Claude, "Claude", "Anthropic", "claude",
        [
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(home, ".claude", "local", "claude.exe"),
            Path.Combine(appData, "npm", "claude.cmd"),
            Path.Combine(appData, "Claude", "claude-code", "*", "claude.exe"),
            Path.Combine(local, "AnthropicClaude", "claude-code", "*", "claude.exe"),
            Path.Combine(home, ".local", "bin", "claude"),
        ], () => new ClaudeAdapter(), "Claude Desktop / Claude Code engine (stream-json)");

        yield return new Spec(Codex, "Codex", "OpenAI", "codex",
        [
            Path.Combine(appData, "npm", "codex.cmd"),
            Path.Combine(local, "Programs", "Codex", "resources", "codex.exe"),
            Path.Combine(local, "Programs", "codex", "resources", "bin", "codex.exe"),
            Path.Combine(home, ".codex", "bin", "codex.exe"),
            Path.Combine(home, ".local", "bin", "codex"),
        ], () => new CodexAdapter(), "Codex Desktop / CLI (app-server JSON-RPC)");

        yield return new Spec(Pi, "Pi", "pi.dev", "pi",
        [
            Path.Combine(appData, "npm", "pi.cmd"),
            Path.Combine(home, ".local", "bin", "pi"),
        ], () => new PiAdapter(), "Pi Coding Agent (RPC mode)");

        yield return new Spec(Opencode, "opencode", "SST", "opencode",
        [
            Path.Combine(home, ".opencode", "bin", "opencode.exe"),
            Path.Combine(appData, "npm", "opencode.cmd"),
            Path.Combine(home, "scoop", "shims", "opencode.exe"),
            Path.Combine(home, ".opencode", "bin", "opencode"),
        ], () => new OpencodeAdapter(), "opencode (headless server + SSE)");
    }

    public IReadOnlyList<AgentInfo> List()
    {
        lock (_lock)
        {
            var list = Specs().Select(s => _cache.TryGetValue(s.Id, out var c) ? c : Probe(s, withVersion: false)).ToList();
            if (_config.EnableEchoAgent)
                list.Add(new AgentInfo(Echo, "Demo agent", "Conduit", true, null, HostInfo.Version, "Scripted agent for trying the app"));
            return list;
        }
    }

    /// <summary>Re-scans PATH and known install locations and reads versions (slow: spawns processes).</summary>
    public async Task<IReadOnlyList<AgentInfo>> RefreshAsync()
    {
        var results = await Task.WhenAll(Specs().Select(s => Task.Run(() => Probe(s, withVersion: true))));
        lock (_lock)
        {
            foreach (var r in results) _cache[r.Id] = r;
        }
        return List();
    }

    private AgentInfo Probe(Spec s, bool withVersion)
    {
        var path = Resolve(s);
        string? version = null;
        if (path is not null && withVersion) version = ReadVersion(path);
        return new AgentInfo(s.Id, s.Name, s.Vendor, path is not null, path, version,
            path is null ? $"Not found — install it or set its path in Agents" : s.Detail);
    }

    private string? Resolve(Spec s)
    {
        if (_config.AgentPaths.TryGetValue(s.Id, out var configured) && File.Exists(configured)) return configured;
        var onPath = FindOnPath(s.Command);
        if (onPath is not null) return onPath;
        foreach (var c in s.Candidates)
        {
            if (string.IsNullOrEmpty(c)) continue;
            if (c.Contains('*'))
            {
                var dir = Path.GetDirectoryName(Path.GetDirectoryName(c))!;
                var file = Path.GetFileName(c);
                if (!Directory.Exists(dir)) continue;
                var hit = Directory.GetDirectories(dir).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists);
                if (hit is not null) return hit;
            }
            else if (File.Exists(c)) return c;
        }
        return null;
    }

    public static string? FindOnPath(string command)
    {
        var exts = OperatingSystem.IsWindows()
            ? (Env("PATHEXT") is { Length: > 0 } pe ? pe : ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var dir in Env("PATH").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir.Trim('"'), command + ext.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static string? ReadVersion(string path)
    {
        try
        {
            var psi = JsonlProcess.CreateStartInfo(path, ["--version"], Environment.CurrentDirectory);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { p.Kill(true); return null; }
            return output.Result.Trim().Split('\n')[0].Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public (IAgentAdapter Adapter, string Path) Create(string agentId)
    {
        if (agentId == Echo && _config.EnableEchoAgent) return (new EchoAdapter(), "");
        var spec = Specs().FirstOrDefault(s => s.Id == agentId) ?? throw new ArgumentException($"Unknown agent '{agentId}'");
        var path = Resolve(spec) ?? throw new InvalidOperationException($"{spec.Name} is not installed on this computer.");
        return (spec.Factory(), path);
    }
}
