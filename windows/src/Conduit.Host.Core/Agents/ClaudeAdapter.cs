using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Conduit.Host.Core.Protocol;

namespace Conduit.Host.Core.Agents;

/// <summary>
/// Claude (Claude Desktop / Claude Code). Claude Desktop does not expose a remote-control API for its chat
/// window, so Conduit drives the Claude Code agent engine that ships with it (same account, settings,
/// CLAUDE.md, MCP servers) via its stream-json protocol:
///
///   claude -p --input-format stream-json --output-format stream-json --verbose
///          --include-partial-messages --permission-prompt-tool stdio [--resume id]
///
/// Tool permission prompts arrive as <c>control_request/can_use_tool</c> and are surfaced as approvals on the phone.
/// </summary>
public sealed class ClaudeAdapter : IAgentAdapter
{
    private JsonlProcess? _proc;
    private IAgentSink _sink = null!;
    private string? _sessionId;
    private string _currentMid = "";
    private readonly Dictionary<string, StringBuilder> _finalText = [];
    private readonly ConcurrentDictionary<string, JsonObject> _pendingPermissions = new();
    private readonly HashSet<string> _alwaysAllow = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _toolNames = [];
    private int _requestCounter;

    public Task StartAsync(AgentStartOptions o, IAgentSink sink, CancellationToken ct)
    {
        _sink = sink;
        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--include-partial-messages", "--permission-prompt-tool", "stdio",
        };
        if (!string.IsNullOrEmpty(o.ResumeToken)) { args.Add("--resume"); args.Add(o.ResumeToken); }
        if (!string.IsNullOrEmpty(o.Model)) { args.Add("--model"); args.Add(o.Model); }
        if (IsMode(o.Mode)) { args.Add("--permission-mode"); args.Add(o.Mode!); }

        _proc = JsonlProcess.Start(o.ExecutablePath, args, o.WorkingDirectory, new Dictionary<string, string>
        {
            ["CLAUDE_CODE_ENTRYPOINT"] = "sdk-conduit",
        });
        _proc.Message += OnMessage;
        _proc.Exited += code => _sink.Exited(code, code is 0 or null ? null : _proc?.StderrTail());
        _proc.Send(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = NextRequestId(),
            ["request"] = new JsonObject { ["subtype"] = "initialize" },
        });
        _sink.Emit(Ev.Status(SessionStates.Idle));
        return Task.CompletedTask;
    }

    private string NextRequestId() => $"conduit_{Interlocked.Increment(ref _requestCounter)}";

    /// <summary>Claude Code permission modes (CLI --permission-mode / control_request set_permission_mode).</summary>
    public static readonly AgentMode[] Modes =
    [
        new("default", "默认", "修改文件和运行命令前先征求同意"),
        new("acceptEdits", "自动接受编辑", "自动批准文件修改，命令仍需同意"),
        new("plan", "计划模式", "只读分析并给出计划，不做修改"),
        new("bypassPermissions", "完全自主", "跳过所有确认（谨慎使用）"),
    ];

    private static bool IsMode(string? m) => m is "acceptEdits" or "plan" or "bypassPermissions" or "default";

    public Task SendAsync(string text, IReadOnlyList<ImageInput> images, CancellationToken ct)
    {
        var content = new JsonArray();
        foreach (var img in images)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = img.MimeType, ["data"] = img.Base64 },
            });
        }
        content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        _proc!.Send(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
            ["parent_tool_use_id"] = null,
            ["session_id"] = _sessionId ?? "default",
        });
        _sink.Emit(Ev.Status(SessionStates.Running, "思考中…"));
        return Task.CompletedTask;
    }

    public Task<bool> UpdateOptionsAsync(string? model, string? mode, CancellationToken ct)
    {
        if (_proc is null) return Task.FromResult(false);
        if (IsMode(mode))
        {
            _proc.Send(new JsonObject
            {
                ["type"] = "control_request",
                ["request_id"] = NextRequestId(),
                ["request"] = new JsonObject { ["subtype"] = "set_permission_mode", ["mode"] = mode },
            });
        }
        if (model is not null)
        {
            _proc.Send(new JsonObject
            {
                ["type"] = "control_request",
                ["request_id"] = NextRequestId(),
                ["request"] = new JsonObject { ["subtype"] = "set_model", ["model"] = model.Length == 0 ? null : model },
            });
        }
        return Task.FromResult(true);
    }

    public Task InterruptAsync(CancellationToken ct)
    {
        _proc?.Send(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = NextRequestId(),
            ["request"] = new JsonObject { ["subtype"] = "interrupt" },
        });
        return Task.CompletedTask;
    }

    public Task RespondApprovalAsync(string approvalId, string decision, CancellationToken ct)
    {
        if (!_pendingPermissions.TryRemove(approvalId, out var req)) return Task.CompletedTask;
        var allow = decision != ApprovalDecisions.Deny;
        if (decision == ApprovalDecisions.AllowAlways && (string?)req["tool_name"] is { } tool)
            _alwaysAllow.Add(tool);
        SendPermission(approvalId, req, allow);
        _sink.Emit(Ev.ApprovalDone(approvalId, decision));
        if (_pendingPermissions.IsEmpty) _sink.Emit(Ev.Status(SessionStates.Running, allow ? "继续执行…" : "已拒绝"));
        return Task.CompletedTask;
    }

    private void SendPermission(string requestId, JsonObject req, bool allow)
    {
        JsonObject response = allow
            ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = req["input"]?.DeepClone() ?? new JsonObject() }
            : new JsonObject { ["behavior"] = "deny", ["message"] = "用户在 Conduit 中拒绝了此操作。" };
        _proc?.Send(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject
            {
                ["subtype"] = "success",
                ["request_id"] = requestId,
                ["response"] = response,
            },
        });
    }

    internal void OnMessage(JsonObject m)
    {
        var type = (string?)m["type"];
        var isSubAgent = m["parent_tool_use_id"] is JsonValue;
        switch (type)
        {
            case "system" when (string?)m["subtype"] == "init":
                if ((string?)m["session_id"] is { Length: > 0 } sid)
                {
                    _sessionId = sid;
                    _sink.SetResumeToken(sid);
                }
                break;

            case "stream_event" when !isSubAgent:
                OnStreamEvent(m["event"] as JsonObject);
                break;

            case "assistant":
                OnAssistant(m["message"] as JsonObject, isSubAgent);
                break;

            case "user":
                OnToolResults(m["message"] as JsonObject);
                break;

            case "result":
                var isError = (bool?)m["is_error"] == true || ((string?)m["subtype"])?.StartsWith("error") == true;
                if (isError)
                {
                    var err = (string?)m["result"] ?? (string?)m["subtype"] ?? "Claude 返回了错误";
                    _sink.Emit(Ev.Error(err));
                    _sink.Emit(Ev.Status(SessionStates.Error, Text.FirstLine(err)));
                }
                else
                {
                    _sink.Emit(Ev.Status(SessionStates.Idle, "已完成"));
                }
                break;

            case "control_request":
                OnControlRequest(m);
                break;
        }
    }

    private void OnStreamEvent(JsonObject? e)
    {
        if (e is null) return;
        switch ((string?)e["type"])
        {
            case "message_start":
                _currentMid = (string?)e["message"]?["id"] ?? Guid.NewGuid().ToString("n");
                break;
            case "content_block_delta":
                var delta = e["delta"] as JsonObject;
                switch ((string?)delta?["type"])
                {
                    case "text_delta":
                        _sink.Emit(Ev.Delta(_currentMid, (string?)delta!["text"] ?? ""));
                        break;
                    case "thinking_delta":
                        _sink.Emit(Ev.Reasoning(_currentMid, (string?)delta!["thinking"] ?? "", isDelta: true));
                        break;
                }
                break;
        }
    }

    private void OnAssistant(JsonObject? msg, bool isSubAgent)
    {
        if (msg is null) return;
        var mid = (string?)msg["id"] ?? _currentMid;
        if (msg["content"] is not JsonArray blocks) return;
        foreach (var b in blocks.OfType<JsonObject>())
        {
            switch ((string?)b["type"])
            {
                case "text" when !isSubAgent:
                    if (!_finalText.TryGetValue(mid, out var sb)) _finalText[mid] = sb = new StringBuilder();
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append((string?)b["text"]);
                    _sink.Emit(Ev.Message(mid, sb.ToString()));
                    break;
                case "thinking" when !isSubAgent:
                    _sink.Emit(Ev.Reasoning(mid, (string?)b["thinking"] ?? "", isDelta: false));
                    break;
                case "tool_use":
                    var tid = (string?)b["id"] ?? Guid.NewGuid().ToString("n");
                    var name = (string?)b["name"] ?? "tool";
                    var input = b["input"];
                    _toolNames[tid] = name;
                    if (name == "TodoWrite" && input?["todos"] is JsonArray todos)
                    {
                        _sink.Emit(Ev.Plan(todos.OfType<JsonObject>().Select(t =>
                            ((string?)t["content"] ?? "", (string?)t["status"] ?? "pending"))));
                        continue;
                    }
                    var title = ToolTitles.For(name, input);
                    _sink.Emit(Ev.Tool(tid, name, isSubAgent ? "↳ " + title : title, "running", ToolTitles.InputPreview(input)));
                    _sink.Emit(Ev.Status(SessionStates.Running, title));
                    break;
            }
        }
    }

    private void OnToolResults(JsonObject? msg)
    {
        if (msg?["content"] is not JsonArray blocks) return;
        foreach (var b in blocks.OfType<JsonObject>())
        {
            if ((string?)b["type"] != "tool_result") continue;
            var tid = (string?)b["tool_use_id"] ?? "";
            if (!_toolNames.TryGetValue(tid, out var name) || name == "TodoWrite") continue;
            var isError = (bool?)b["is_error"] == true;
            // Only status + output: the phone merges this into the existing tool row (same tid).
            var ev = Ev.Tool(tid, name, "", isError ? "error" : "ok", null, ToolTitles.ResultText(b["content"]));
            ev.Remove("title");
            _sink.Emit(ev);
        }
    }

    private void OnControlRequest(JsonObject m)
    {
        var requestId = (string?)m["request_id"] ?? "";
        var req = m["request"] as JsonObject;
        if ((string?)req?["subtype"] != "can_use_tool")
        {
            // Hooks / MCP messages we don't implement: answer so the CLI never blocks.
            _proc?.Send(new JsonObject
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = requestId, ["error"] = "Not supported by Conduit" },
            });
            return;
        }

        var tool = (string?)req["tool_name"] ?? "tool";
        if (_alwaysAllow.Contains(tool))
        {
            SendPermission(requestId, req, allow: true);
            return;
        }
        _pendingPermissions[requestId] = req;
        var title = ToolTitles.For(tool, req["input"]);
        _sink.Emit(Ev.Approval(requestId, title, ToolTitles.InputPreview(req["input"]), KindFor(tool)));
        _sink.Emit(Ev.Status(SessionStates.Waiting, "需要批准：" + title));
    }

    private static string KindFor(string tool) => tool.ToLowerInvariant() switch
    {
        "bash" => "command",
        "edit" or "multiedit" or "write" or "notebookedit" => "edit",
        "webfetch" or "websearch" => "network",
        _ => "tool",
    };

    public async ValueTask DisposeAsync()
    {
        if (_proc is not null) await _proc.DisposeAsync();
    }
}
