using System.Text.Json.Nodes;

namespace Conduit.Host.Core.Agents;

public sealed record AgentStartOptions(
    string SessionId,
    string WorkingDirectory,
    string ExecutablePath,
    string? ResumeToken,
    string? Model);

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
    Task SendAsync(string text, CancellationToken ct);
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
    string Detail);
