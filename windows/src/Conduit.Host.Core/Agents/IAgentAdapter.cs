using System.Text.Json.Nodes;

namespace Conduit.Host.Core.Agents;

public sealed record AgentStartOptions(
    string SessionId,
    string WorkingDirectory,
    string ExecutablePath,
    string? ResumeToken,
    string? Model,
    string? Mode = null);

/// <summary>An image attached from the phone (base64, e.g. a screenshot of a bug).</summary>
public sealed record ImageInput(string MimeType, string Base64)
{
    public string Extension => MimeType switch { "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".png" };

    /// <summary>Writes the image to a temp file for agents that take paths.</summary>
    public string SaveToTemp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "conduit-images");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("n") + Extension);
        File.WriteAllBytes(path, Convert.FromBase64String(Base64));
        return path;
    }
}

/// <summary>Where adapters report what the agent is doing.</summary>
public interface IAgentSink
{
    void Emit(JsonObject ev);
    void SetResumeToken(string token);
    /// <summary>Called when the agent process exits on its own.</summary>
    void Exited(int? exitCode, string? reason);
}

/// <summary>
/// One running agent conversation. Implementations wrap a native protocol:
///   Claude Code  — stream-json over stdio (+ stdio permission prompts)
///   Codex        — `codex app-server` JSON-RPC over stdio
///   Pi           — `pi --mode rpc` JSONL over stdio
///   opencode     — `opencode serve` HTTP + SSE
/// </summary>
public interface IAgentAdapter : IAsyncDisposable
{
    Task StartAsync(AgentStartOptions options, IAgentSink sink, CancellationToken ct);
    Task SendAsync(string text, IReadOnlyList<ImageInput> images, CancellationToken ct);
    /// <summary>Applies a new model / permission mode. Returns false if the agent must be restarted to apply it.</summary>
    Task<bool> UpdateOptionsAsync(string? model, string? mode, CancellationToken ct);
    Task InterruptAsync(CancellationToken ct);
    Task RespondApprovalAsync(string approvalId, string decision, CancellationToken ct);
}

public sealed record AgentInfo(
    string Id,
    string Name,
    string Vendor,
    bool Available,
    string? Path,
    string? Version,
    string Detail,
    IReadOnlyList<AgentMode>? Modes = null,
    IReadOnlyList<string>? Models = null);

/// <summary>A permission / autonomy mode offered in the phone's composer (e.g. Claude's "计划模式").</summary>
public sealed record AgentMode(string Id, string Label, string Description);
