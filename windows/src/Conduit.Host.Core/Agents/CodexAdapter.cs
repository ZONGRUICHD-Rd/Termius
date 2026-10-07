using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Conduit.Host.Core.Protocol;

namespace Conduit.Host.Core.Agents;

/// <summary>
/// Codex (Codex Desktop / CLI) through <c>codex app-server</c> — the same JSON-RPC interface the Codex
/// desktop app and IDE extensions use. Threads map to Conduit sessions; approvals arrive as server requests.
/// </summary>
public sealed class CodexAdapter : IAgentAdapter
{
    private JsonlProcess? _proc;
    private IAgentSink _sink = null!;
    private string _cwd = "";
    private string? _model;
    private string? _threadId;
    private string? _turnId;
    private long _nextId;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<string, (JsonNode Id, string Method, JsonObject Params)> _approvals = new();

    public async Task StartAsync(AgentStartOptions o, IAgentSink sink, CancellationToken ct)
    {
        _sink = sink;
        _cwd = o.WorkingDirectory;
        _model = o.Model;
        _proc = JsonlProcess.Start(o.ExecutablePath, ["app-server"], o.WorkingDirectory);
        _proc.Message += OnMessage;
        _proc.Exited += code =>
        {
            foreach (var p in _pending.Values) p.TrySetException(new InvalidOperationException("codex app-server exited"));
            _sink.Exited(code, code is 0 or null ? null : _proc?.StderrTail());
        };

        await RequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = "conduit", ["title"] = "Conduit", ["version"] = HostInfo.Version },
        }, ct);
        _proc.Send(new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() });

        JsonNode? result = null;
        if (!string.IsNullOrEmpty(o.ResumeToken))
        {
            try { result = await RequestAsync("thread/resume", new JsonObject { ["threadId"] = o.ResumeToken, ["cwd"] = _cwd }, ct); }
            catch (AgentRpcException) { _sink.Emit(Ev.Notice("Could not resume the previous Codex thread — started a new one.")); }
        }
        if (result is null)
        {
            var p = new JsonObject { ["cwd"] = _cwd };
            if (!string.IsNullOrEmpty(_model)) p["model"] = _model;
            result = await RequestAsync("thread/start", p, ct);
        }
        _threadId = (string?)result?["thread"]?["id"] ?? throw new InvalidOperationException("Codex did not return a thread id");
        _sink.SetResumeToken(_threadId);
        _sink.Emit(Ev.Status(SessionStates.Idle));
    }

    private async Task<JsonNode?> RequestAsync(string method, JsonObject @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        _proc!.Send(new JsonObject { ["method"] = method, ["id"] = id, ["params"] = @params });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await using var _ = timeout.Token.Register(() => tcs.TrySetCanceled());
        try { return await tcs.Task; }
        finally { _pending.TryRemove(id, out var _); }
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        var input = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        _sink.Emit(Ev.Status(SessionStates.Running, "Thinking…"));
        if (_turnId is not null)
        {
            try
            {
                await RequestAsync("turn/steer", new JsonObject { ["threadId"] = _threadId, ["input"] = input, ["expectedTurnId"] = _turnId }, ct);
                return;
            }
            catch (AgentRpcException) { /* turn already ended: fall through to a new turn */ }
        }
        var res = await RequestAsync("turn/start", new JsonObject { ["threadId"] = _threadId, ["input"] = input, ["cwd"] = _cwd }, ct);
        _turnId ??= (string?)res?["turn"]?["id"];
    }

    public async Task InterruptAsync(CancellationToken ct)
    {
        if (_turnId is null || _threadId is null) return;
        await RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = _threadId, ["turnId"] = _turnId }, ct);
    }

    public Task RespondApprovalAsync(string approvalId, string decision, CancellationToken ct)
    {
        if (!_approvals.TryRemove(approvalId, out var a)) return Task.CompletedTask;
        JsonObject result;
        if (a.Method == "item/permissions/requestApproval")
        {
            result = decision == ApprovalDecisions.Deny
                ? new JsonObject { ["permissions"] = new JsonObject() }
                : new JsonObject
                {
                    ["permissions"] = a.Params["permissions"]?.DeepClone() ?? new JsonObject(),
                    ["scope"] = decision == ApprovalDecisions.AllowAlways ? "session" : "turn",
                };
        }
        else
        {
            result = new JsonObject
            {
                ["decision"] = decision switch
                {
                    ApprovalDecisions.AllowAlways => "acceptForSession",
                    ApprovalDecisions.Allow => "accept",
                    _ => "decline",
                },
            };
        }
        _proc?.Send(new JsonObject { ["id"] = a.Id.DeepClone(), ["result"] = result });
        _sink.Emit(Ev.ApprovalDone(approvalId, decision));
        if (_approvals.IsEmpty) _sink.Emit(Ev.Status(SessionStates.Running, "Continuing…"));
        return Task.CompletedTask;
    }

    internal void OnMessage(JsonObject m)
    {
        var method = (string?)m["method"];
        var idNode = m["id"];

        if (method is null && idNode is not null)
        {
            // Response to one of our requests.
            if (idNode is JsonValue v && v.TryGetValue<long>(out var id) && _pending.TryGetValue(id, out var tcs))
            {
                if (m["error"] is JsonObject err) tcs.TrySetException(new AgentRpcException((string?)err["message"] ?? "error"));
                else tcs.TrySetResult(m["result"]);
            }
            return;
        }

        var p = m["params"] as JsonObject ?? new JsonObject();
        if (idNode is not null && method is not null)
        {
            OnServerRequest(idNode, method, p);
            return;
        }

        switch (method)
        {
            case "turn/started":
                _turnId = (string?)p["turn"]?["id"] ?? _turnId;
                _sink.Emit(Ev.Status(SessionStates.Running, "Working…"));
                break;

            case "turn/completed":
                _turnId = null;
                var turn = p["turn"] as JsonObject;
                switch ((string?)turn?["status"])
                {
                    case "failed":
                        var msg = (string?)turn?["error"]?["message"] ?? "Turn failed";
                        _sink.Emit(Ev.Error(msg));
                        _sink.Emit(Ev.Status(SessionStates.Error, Text.FirstLine(msg)));
                        break;
                    case "interrupted":
                        _sink.Emit(Ev.Status(SessionStates.Idle, "Interrupted"));
                        break;
                    default:
                        _sink.Emit(Ev.Status(SessionStates.Idle, "Done"));
                        break;
                }
                break;

            case "item/agentMessage/delta":
                _sink.Emit(Ev.Delta((string?)p["itemId"] ?? "", (string?)p["delta"] ?? ""));
                break;

            case "item/reasoning/summaryTextDelta":
                _sink.Emit(Ev.Reasoning((string?)p["itemId"] ?? "", (string?)p["delta"] ?? "", isDelta: true));
                break;

            case "item/started":
            case "item/completed":
                if (p["item"] is JsonObject item) OnItem(item, method == "item/completed");
                break;

            case "turn/plan/updated":
                if (p["plan"] is JsonArray plan)
                    _sink.Emit(Ev.Plan(plan.OfType<JsonObject>().Select(s => ((string?)s["step"] ?? "", (string?)s["status"] ?? "pending"))));
                break;

            case "error":
                var em = (string?)p["error"]?["message"];
                if (em is not null) _sink.Emit(Ev.Error(em));
                break;

            case "serverRequest/resolved":
                var rid = p["requestId"]?.ToString();
                if (rid is not null && _approvals.TryRemove(rid, out _)) _sink.Emit(Ev.ApprovalDone(rid, "resolved"));
                break;
        }
    }

    private void OnItem(JsonObject item, bool completed)
    {
        var id = (string?)item["id"] ?? "";
        var status = MapStatus((string?)item["status"], completed);
        switch ((string?)item["type"])
        {
            case "agentMessage":
                if (completed) _sink.Emit(Ev.Message(id, (string?)item["text"] ?? ""));
                break;

            case "reasoning":
                if (completed && item["summary"] is JsonArray summary && summary.Count > 0)
                    _sink.Emit(Ev.Reasoning(id, string.Join("\n\n", summary.Select(s => s is JsonObject so ? (string?)so["text"] : s?.ToString())), isDelta: false));
                break;

            case "commandExecution":
                var cmd = item["command"] is JsonArray parts ? string.Join(' ', parts.Select(x => x?.ToString())) : (string?)item["command"] ?? "";
                var title = "Run " + Text.FirstLine(cmd, 80);
                _sink.Emit(Ev.Tool(id, "shell", title, status, cmd, (string?)item["aggregatedOutput"]));
                if (!completed) _sink.Emit(Ev.Status(SessionStates.Running, title));
                break;

            case "fileChange":
                var changes = item["changes"] as JsonArray ?? [];
                var files = changes.OfType<JsonObject>().Select(c => Text.FileName((string?)c["path"])).ToList();
                var t = files.Count switch
                {
                    0 => "Edit files",
                    1 => "Edit " + files[0],
                    _ => $"Edit {files[0]} +{files.Count - 1}",
                };
                var diff = string.Join("\n", changes.OfType<JsonObject>().Select(c => (string?)c["diff"] ?? ""));
                _sink.Emit(Ev.Tool(id, "edit", t, status, null, diff));
                if (!completed) _sink.Emit(Ev.Status(SessionStates.Running, t));
                break;

            case "mcpToolCall":
                var name = $"{item["server"]}.{item["tool"]}";
                _sink.Emit(Ev.Tool(id, name, name, status, item["arguments"]?.ToJsonString(), ToolTitles.ResultText(item["result"] ?? item["error"])));
                break;

            case "webSearch":
                _sink.Emit(Ev.Tool(id, "web_search", "Web search " + Text.Clip((string?)item["query"] ?? "", 60), status));
                break;

            case "contextCompaction":
                if (completed) _sink.Emit(Ev.Notice("Context compacted"));
                break;
        }
    }

    private static string MapStatus(string? s, bool completed) => s switch
    {
        "failed" or "declined" => "error",
        "completed" => "ok",
        "inProgress" => "running",
        _ => completed ? "ok" : "running",
    };

    private void OnServerRequest(JsonNode id, string method, JsonObject p)
    {
        var aid = id.ToString();
        switch (method)
        {
            case "item/commandExecution/requestApproval":
            {
                var cmd = p["command"] is JsonArray parts ? string.Join(' ', parts.Select(x => x?.ToString())) : (string?)p["command"];
                var host = (string?)p["networkApprovalContext"]?["host"];
                var title = host is not null ? $"Network access to {host}" : "Run " + Text.FirstLine(cmd ?? "command", 80);
                var detail = string.Join("\n", new[] { cmd, (string?)p["reason"], p["cwd"] is null ? null : $"in {p["cwd"]}" }.Where(x => !string.IsNullOrEmpty(x)));
                _approvals[aid] = (id.DeepClone(), method, p);
                _sink.Emit(Ev.Approval(aid, title, detail, host is not null ? "network" : "command"));
                _sink.Emit(Ev.Status(SessionStates.Waiting, "Needs approval: " + title));
                break;
            }
            case "item/fileChange/requestApproval":
            {
                _approvals[aid] = (id.DeepClone(), method, p);
                _sink.Emit(Ev.Approval(aid, "Apply file changes", (string?)p["reason"] ?? "Codex wants to modify files in the workspace.", "edit"));
                _sink.Emit(Ev.Status(SessionStates.Waiting, "Needs approval: file changes"));
                break;
            }
            case "item/permissions/requestApproval":
            {
                _approvals[aid] = (id.DeepClone(), method, p);
                _sink.Emit(Ev.Approval(aid, "Grant extra permissions", (string?)p["reason"] ?? p["permissions"]?.ToJsonString() ?? "", "permissions"));
                _sink.Emit(Ev.Status(SessionStates.Waiting, "Needs approval: permissions"));
                break;
            }
            default:
                _proc?.Send(new JsonObject
                {
                    ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"{method} is not supported by Conduit" },
                });
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_proc is not null) await _proc.DisposeAsync();
    }
}

public sealed class AgentRpcException(string message) : Exception(message);
