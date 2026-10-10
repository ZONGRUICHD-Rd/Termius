using System.Text.Json.Nodes;
using Conduit.Host.Core.Protocol;

namespace Conduit.Host.Core.Agents;

/// <summary>
/// A scripted agent with no external dependencies. Exercises every event type (streaming, tools, plan,
/// approvals) so the phone UI and the Dynamic Island can be tried without installing a real agent.
/// Enable it in Settings → Developer → Demo agent.
/// </summary>
public sealed class EchoAdapter : IAgentAdapter
{
    private IAgentSink _sink = null!;
    private CancellationTokenSource? _turn;
    private TaskCompletionSource<string>? _approval;
    private string? _approvalId;
    private int _n;

    public Task StartAsync(AgentStartOptions options, IAgentSink sink, CancellationToken ct)
    {
        _sink = sink;
        _sink.SetResumeToken(options.SessionId);
        _sink.Emit(Ev.Status(SessionStates.Idle));
        return Task.CompletedTask;
    }

    public Task<bool> UpdateOptionsAsync(string? model, string? mode, CancellationToken ct) => Task.FromResult(true);

    public Task SendAsync(string text, IReadOnlyList<ImageInput> images, CancellationToken ct)
    {
        _turn?.Cancel();
        _turn = new CancellationTokenSource();
        var token = _turn.Token;
        _ = Task.Run(() => RunTurn(text, token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunTurn(string text, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _n);
        try
        {
            var steps = new[] { "Read the request", "Inspect the project", "Make the change", "Verify" };
            var status = steps.Select(s => (s, "pending")).ToArray();
            void Plan(int active)
            {
                for (var i = 0; i < status.Length; i++)
                    status[i].Item2 = i < active ? "completed" : i == active ? "in_progress" : "pending";
                _sink.Emit(Ev.Plan(status));
            }

            _sink.Emit(Ev.Status(SessionStates.Running, "Thinking…"));
            Plan(0);
            await Task.Delay(500, ct);

            Plan(1);
            _sink.Emit(Ev.Tool($"t{n}a", "Read", "Read README.md", "running", "README.md"));
            _sink.Emit(Ev.Status(SessionStates.Running, "Read README.md"));
            await Task.Delay(700, ct);
            _sink.Emit(Ev.Tool($"t{n}a", "Read", "Read README.md", "ok", "README.md", "# Demo project\nA tiny sample."));

            Plan(2);
            _approvalId = $"a{n}";
            _approval = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sink.Emit(Ev.Approval(_approvalId, "Run echo hello", "echo hello", "command"));
            _sink.Emit(Ev.Status(SessionStates.Waiting, "Needs approval: Run echo hello"));
            var decision = await _approval.Task.WaitAsync(ct);
            _sink.Emit(Ev.ApprovalDone(_approvalId, decision));
            _sink.Emit(Ev.Status(SessionStates.Running, "Run echo hello"));
            _sink.Emit(Ev.Tool($"t{n}b", "Bash", "Run echo hello", decision == ApprovalDecisions.Deny ? "error" : "ok", "echo hello",
                decision == ApprovalDecisions.Deny ? "Denied by user" : "hello"));

            Plan(3);
            var mid = $"m{n}";
            var reply = $"You said: **{text}**\n\nHere is what I did:\n\n1. Read the README\n2. Ran a command\n\n```swift\nprint(\"hello from Conduit\")\n```\n";
            foreach (var chunk in Chunk(reply, 6))
            {
                _sink.Emit(Ev.Delta(mid, chunk));
                await Task.Delay(30, ct);
            }
            _sink.Emit(Ev.Message(mid, reply));
            Plan(4);
            _sink.Emit(Ev.Status(SessionStates.Idle, "Done"));
        }
        catch (OperationCanceledException)
        {
            _sink.Emit(Ev.Status(SessionStates.Idle, "Interrupted"));
        }
    }

    private static IEnumerable<string> Chunk(string s, int size)
    {
        for (var i = 0; i < s.Length; i += size) yield return s.Substring(i, Math.Min(size, s.Length - i));
    }

    public Task InterruptAsync(CancellationToken ct)
    {
        _turn?.Cancel();
        return Task.CompletedTask;
    }

    public Task RespondApprovalAsync(string approvalId, string decision, CancellationToken ct)
    {
        if (approvalId == _approvalId) _approval?.TrySetResult(decision);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _turn?.Cancel();
        return ValueTask.CompletedTask;
    }
}
