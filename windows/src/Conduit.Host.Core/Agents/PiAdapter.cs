using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Conduit.Host.Core.Protocol;

namespace Conduit.Host.Core.Agents;

/// <summary>
/// Pi Coding Agent via <c>pi --mode rpc --session-id &lt;id&gt;</c> (JSONL commands / responses / events).
/// The Conduit session id doubles as Pi's project session id, so restarting the host resumes the same conversation.
/// Extension dialogs (<c>confirm</c> / <c>select</c>) are surfaced as approvals.
/// </summary>
public sealed class PiAdapter : IAgentAdapter
{
    private JsonlProcess? _proc;
    private IAgentSink _sink = null!;
    private bool _running;
    private int _messageCounter;
    private string _mid = "";
    private readonly StringBuilder _text = new();
    private readonly ConcurrentDictionary<string, JsonObject> _dialogs = new();
    private int _reqCounter;

    public Task StartAsync(AgentStartOptions o, IAgentSink sink, CancellationToken ct)
    {
        _sink = sink;
        var args = new List<string> { "--mode", "rpc", "--session-id", "conduit-" + o.SessionId };
        if (!string.IsNullOrEmpty(o.Model)) { args.Add("--model"); args.Add(o.Model); }
        _proc = JsonlProcess.Start(o.ExecutablePath, args, o.WorkingDirectory);
        _proc.Message += OnMessage;
        _proc.Exited += code => _sink.Exited(code, code is 0 or null ? null : _proc?.StderrTail());
        _sink.SetResumeToken("conduit-" + o.SessionId);
        _sink.Emit(Ev.Status(SessionStates.Idle));
        return Task.CompletedTask;
    }

    private string NextId() => $"c{Interlocked.Increment(ref _reqCounter)}";

    public Task SendAsync(string text, IReadOnlyList<ImageInput> images, CancellationToken ct)
    {
        var cmd = new JsonObject { ["id"] = NextId(), ["type"] = "prompt", ["message"] = text };
        if (images.Count > 0)
            cmd["images"] = new JsonArray(images.Select(i => (JsonNode)new JsonObject { ["type"] = "image", ["data"] = i.Base64, ["mimeType"] = i.MimeType }).ToArray());
        if (_running) cmd["streamingBehavior"] = "steer";
        _proc!.Send(cmd);
        _sink.Emit(Ev.Status(SessionStates.Running, "思考中…"));
        return Task.CompletedTask;
    }

    /// <summary>Pi picks its model at start-up (--model), so a model change restarts it on the same session id.</summary>
    public Task<bool> UpdateOptionsAsync(string? model, string? mode, CancellationToken ct) => Task.FromResult(model is null);

    public Task InterruptAsync(CancellationToken ct)
    {
        _proc?.Send(new JsonObject { ["id"] = NextId(), ["type"] = "abort" });
        return Task.CompletedTask;
    }

    public Task RespondApprovalAsync(string approvalId, string decision, CancellationToken ct)
    {
        if (!_dialogs.TryRemove(approvalId, out var req)) return Task.CompletedTask;
        var allow = decision != ApprovalDecisions.Deny;
        var resp = new JsonObject { ["type"] = "extension_ui_response", ["id"] = approvalId };
        if ((string?)req["method"] == "select" && req["options"] is JsonArray opts && opts.Count > 0)
        {
            // Convention: first option is the affirmative one ("Allow"), last the negative one ("Block").
            resp["value"] = (allow ? opts[0] : opts[^1])?.DeepClone();
        }
        else
        {
            resp["confirmed"] = allow;
        }
        _proc?.Send(resp);
        _sink.Emit(Ev.ApprovalDone(approvalId, decision));
        if (_dialogs.IsEmpty && _running) _sink.Emit(Ev.Status(SessionStates.Running, "继续执行…"));
        return Task.CompletedTask;
    }

    internal void OnMessage(JsonObject m)
    {
        switch ((string?)m["type"])
        {
            case "response":
                if ((bool?)m["success"] == false)
                {
                    var err = (string?)m["error"] ?? "Pi 拒绝了该命令";
                    _sink.Emit(Ev.Error(err));
                    if (!_running) _sink.Emit(Ev.Status(SessionStates.Idle, Text.FirstLine(err)));
                }
                else if ((string?)m["command"] == "prompt" && (string?)m["data"]?["disposition"] == "handled" && !_running)
                {
                    _sink.Emit(Ev.Status(SessionStates.Idle));
                }
                break;

            case "agent_start":
                _running = true;
                _sink.Emit(Ev.Status(SessionStates.Running, "工作中…"));
                break;

            case "agent_settled":
                _running = false;
                _sink.Emit(Ev.Status(SessionStates.Idle, "已完成"));
                break;

            case "message_start" when ((string?)m["message"]?["role"]) == "assistant":
                _mid = $"pi-{Interlocked.Increment(ref _messageCounter)}";
                _text.Clear();
                break;

            case "message_update":
                var e = m["assistantMessageEvent"] as JsonObject;
                switch ((string?)e?["type"])
                {
                    case "text_delta":
                        var d = (string?)e!["delta"] ?? "";
                        _text.Append(d);
                        _sink.Emit(Ev.Delta(_mid, d));
                        break;
                    case "thinking_delta":
                        _sink.Emit(Ev.Reasoning(_mid, (string?)e!["delta"] ?? "", isDelta: true));
                        break;
                }
                break;

            case "message_end" when ((string?)m["message"]?["role"]) == "assistant":
                var final = ExtractText(m["message"]?["content"]);
                if (final.Length > 0 || _text.Length > 0) _sink.Emit(Ev.Message(_mid, final.Length > 0 ? final : _text.ToString()));
                var stop = (string?)m["message"]?["stopReason"];
                if (stop == "error" && (string?)m["message"]?["errorMessage"] is { } em) _sink.Emit(Ev.Error(em));
                break;

            case "tool_execution_start":
            {
                var tid = (string?)m["toolCallId"] ?? NextId();
                var name = (string?)m["toolName"] ?? "tool";
                var title = ToolTitles.For(name, m["args"]);
                _sink.Emit(Ev.Tool(tid, name, title, "running", ToolTitles.InputPreview(m["args"])));
                _sink.Emit(Ev.Status(SessionStates.Running, title));
                break;
            }

            case "tool_execution_end":
            {
                var tid = (string?)m["toolCallId"] ?? "";
                var ev = Ev.Tool(tid, (string?)m["toolName"] ?? "tool", "", (bool?)m["isError"] == true ? "error" : "ok", null, ToolTitles.ResultText(m["result"]));
                ev.Remove("title");
                _sink.Emit(ev);
                break;
            }

            case "auto_retry_start":
                _sink.Emit(Ev.Status(SessionStates.Running, $"重试中（{m["attempt"]}/{m["maxAttempts"]}）…"));
                break;

            case "compaction_start":
                _sink.Emit(Ev.Status(SessionStates.Running, "正在压缩上下文…"));
                break;

            case "extension_ui_request":
                OnExtensionUi(m);
                break;
        }
    }

    private void OnExtensionUi(JsonObject m)
    {
        var id = (string?)m["id"] ?? "";
        var method = (string?)m["method"];
        switch (method)
        {
            case "confirm":
            case "select":
                _dialogs[id] = m;
                var title = (string?)m["title"] ?? "Pi 需要确认";
                var detail = (string?)m["message"] ?? (m["options"] is JsonArray o ? string.Join(" / ", o.Select(x => x?.ToString())) : "");
                _sink.Emit(Ev.Approval(id, title, detail, "tool", canAlways: false));
                _sink.Emit(Ev.Status(SessionStates.Waiting, title));
                break;
            case "notify":
                if ((string?)m["message"] is { } note) _sink.Emit(Ev.Notice(note));
                break;
            case "input":
            case "editor":
                // Free-form dialogs are not supported remotely: cancel so Pi never blocks.
                _proc?.Send(new JsonObject { ["type"] = "extension_ui_response", ["id"] = id, ["cancelled"] = true });
                break;
        }
    }

    private static string ExtractText(JsonNode? content) => content switch
    {
        JsonValue v => v.ToString(),
        JsonArray a => string.Join("\n\n", a.OfType<JsonObject>().Where(b => (string?)b["type"] == "text").Select(b => (string?)b["text"] ?? "")),
        _ => "",
    };

    public async ValueTask DisposeAsync()
    {
        if (_proc is not null) await _proc.DisposeAsync();
    }
}
