using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conduit.Host.Core.Protocol;

namespace Conduit.Host.Core.Agents;

/// <summary>
/// opencode via its headless server: <c>opencode serve --hostname 127.0.0.1 --port N</c>.
/// Prompts go to <c>POST /session/:id/prompt_async</c>, progress streams from <c>GET /event</c> (SSE),
/// permissions are answered through the permissions endpoint (v1 and v2 routes are both supported).
/// The server is bound to loopback and protected with a random basic-auth password.
/// </summary>
public sealed class OpencodeAdapter : IAgentAdapter
{
    private JsonlProcess? _proc;
    private IAgentSink _sink = null!;
    private HttpClient _http = null!;
    private string _sessionId = "";
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, string> _messageRoles = new();
    private readonly ConcurrentDictionary<string, bool> _permissions = new();
    private string? _lastPrompt;
    private string? _model;
    private string? _mode;
    private bool _busy;

    public async Task StartAsync(AgentStartOptions o, IAgentSink sink, CancellationToken ct)
    {
        _sink = sink;
        _model = o.Model;
        _mode = o.Mode;
        var port = FreePort();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _proc = JsonlProcess.Start(o.ExecutablePath, ["serve", "--hostname", "127.0.0.1", "--port", port.ToString()], o.WorkingDirectory,
            new Dictionary<string, string> { ["OPENCODE_SERVER_PASSWORD"] = password, ["OPENCODE_SERVER_USERNAME"] = "opencode" });
        _proc.Exited += code =>
        {
            _cts.Cancel();
            _sink.Exited(code, code is 0 or null ? null : _proc?.StderrTail());
        };

        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password)));
        // Pin the project directory for servers that multiplex instances by directory.
        _http.DefaultRequestHeaders.Add("x-opencode-directory", o.WorkingDirectory);

        await WaitUntilReady(ct);

        if (!string.IsNullOrEmpty(o.ResumeToken))
        {
            using var r = await _http.GetAsync($"/session/{o.ResumeToken}", ct);
            if (r.IsSuccessStatusCode) _sessionId = o.ResumeToken;
        }
        if (_sessionId.Length == 0)
        {
            using var r = await _http.PostAsJsonAsync("/session", new { title = "Conduit" }, ct);
            r.EnsureSuccessStatusCode();
            var session = await r.Content.ReadFromJsonAsync<JsonObject>(ct);
            _sessionId = (string?)session?["id"] ?? throw new InvalidOperationException("opencode did not return a session id");
        }
        _sink.SetResumeToken(_sessionId);
        _ = Task.Run(EventLoop);
        _sink.Emit(Ev.Status(SessionStates.Idle));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task WaitUntilReady(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var r = await _http.GetAsync("/session", ct);
                if (r.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("opencode server did not start within 30s: " + _proc?.StderrTail());
    }

    /// <summary>opencode's built-in primary agents.</summary>
    public static readonly AgentMode[] Modes =
    [
        new("build", "构建", "默认模式，可以修改文件和运行命令"),
        new("plan", "计划", "只分析和规划，不做修改"),
    ];

    public async Task SendAsync(string text, IReadOnlyList<ImageInput> images, CancellationToken ct)
    {
        _lastPrompt = text;
        _sink.Emit(Ev.Status(SessionStates.Running, "思考中…"));
        var parts = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var img in images)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "file",
                ["mime"] = img.MimeType,
                ["filename"] = "image" + img.Extension,
                ["url"] = $"data:{img.MimeType};base64,{img.Base64}",
            });
        }
        var body = new JsonObject { ["parts"] = parts };
        if (_mode is "build" or "plan") body["agent"] = _mode;
        if (_model is { } m && m.Contains('/'))
            body["model"] = new JsonObject { ["providerID"] = m[..m.IndexOf('/')], ["modelID"] = m[(m.IndexOf('/') + 1)..] };
        using var r = await _http.PostAsync($"/session/{_sessionId}/prompt_async", Json(body), ct);
        if (r.StatusCode == HttpStatusCode.NotFound)
        {
            // Older servers: the synchronous endpoint streams over SSE anyway, so don't await the reply.
            _ = _http.PostAsync($"/session/{_sessionId}/message", Json(body), _cts.Token);
        }
        else if (!r.IsSuccessStatusCode)
        {
            var err = await r.Content.ReadAsStringAsync(ct);
            _sink.Emit(Ev.Error($"opencode: {(int)r.StatusCode} {Text.Clip(err, 300)}"));
            _sink.Emit(Ev.Status(SessionStates.Error));
        }
    }

    public Task<bool> UpdateOptionsAsync(string? model, string? mode, CancellationToken ct)
    {
        if (model is not null) _model = model.Length == 0 ? null : model;
        if (mode is not null) _mode = mode;
        return Task.FromResult(true);
    }

    public async Task InterruptAsync(CancellationToken ct)
    {
        using var _ = await _http.PostAsync($"/session/{_sessionId}/abort", null, ct);
    }

    public async Task RespondApprovalAsync(string approvalId, string decision, CancellationToken ct)
    {
        _permissions.TryRemove(approvalId, out _);
        var reply = decision switch
        {
            ApprovalDecisions.AllowAlways => "always",
            ApprovalDecisions.Allow => "once",
            _ => "reject",
        };
        using var r = await _http.PostAsync($"/session/{_sessionId}/permissions/{approvalId}", Json(new JsonObject { ["response"] = reply }), ct);
        if (r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            using var r2 = await _http.PostAsync($"/permission/{approvalId}/reply", Json(new JsonObject { ["reply"] = reply }), ct);
        }
        _sink.Emit(Ev.ApprovalDone(approvalId, decision));
        if (_permissions.IsEmpty && _busy) _sink.Emit(Ev.Status(SessionStates.Running, "继续执行…"));
    }

    private static StringContent Json(JsonNode n) => new(n.ToJsonString(), Encoding.UTF8, "application/json");

    private async Task EventLoop()
    {
        var backoff = 250;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/event");
                req.Headers.Accept.ParseAdd("text/event-stream");
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                resp.EnsureSuccessStatusCode();
                backoff = 250;
                await using var stream = await resp.Content.ReadAsStreamAsync(_cts.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var data = new StringBuilder();
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        data.Append(line.AsSpan(5).TrimStart());
                    }
                    else if (line.Length == 0 && data.Length > 0)
                    {
                        Dispatch(data.ToString());
                        data.Clear();
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { }
            await Task.Delay(backoff);
            backoff = Math.Min(backoff * 2, 5000);
        }
    }

    private void Dispatch(string json)
    {
        JsonObject? e;
        try { e = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return; }
        if (e is not null) OnEvent(e);
    }

    internal void OnEvent(JsonObject e)
    {
        var type = (string?)e["type"];
        var p = e["properties"] as JsonObject ?? new JsonObject();
        string? Sid(JsonNode? n) => (string?)n?["sessionID"];

        switch (type)
        {
            case "message.updated":
                if (p["info"] is JsonObject info && Sid(info) == _sessionId && (string?)info["id"] is { } mid)
                    _messageRoles[mid] = (string?)info["role"] ?? "assistant";
                break;

            case "message.part.updated":
                if (p["part"] is JsonObject part && Sid(part) == _sessionId) OnPart(part, (string?)p["delta"]);
                break;

            case "message.part.delta":
                if (Sid(p) == _sessionId && (string?)p["field"] is null or "text")
                {
                    var role = _messageRoles.GetValueOrDefault((string?)p["messageID"] ?? "", "assistant");
                    if (role == "assistant") _sink.Emit(Ev.Delta((string?)p["partID"] ?? "", (string?)p["delta"] ?? ""));
                }
                break;

            case "session.status":
                if (Sid(p) != _sessionId) break;
                switch ((string?)p["status"]?["type"])
                {
                    case "busy":
                        _busy = true;
                        if (_permissions.IsEmpty) _sink.Emit(Ev.Status(SessionStates.Running));
                        break;
                    case "retry":
                        _sink.Emit(Ev.Status(SessionStates.Running, "重试中：" + (string?)p["status"]?["message"]));
                        break;
                    case "idle":
                        _busy = false;
                        _sink.Emit(Ev.Status(SessionStates.Idle, "已完成"));
                        break;
                }
                break;

            case "session.idle":
                if (Sid(p) != _sessionId) break;
                _busy = false;
                _sink.Emit(Ev.Status(SessionStates.Idle, "已完成"));
                break;

            case "session.error":
                if (Sid(p) is { } s && s != _sessionId) break;
                var msg = (string?)p["error"]?["data"]?["message"] ?? (string?)p["error"]?["name"] ?? "opencode 出错";
                _sink.Emit(Ev.Error(msg));
                break;

            case "todo.updated":
                if (Sid(p) == _sessionId && p["todos"] is JsonArray todos)
                    _sink.Emit(Ev.Plan(todos.OfType<JsonObject>().Select(t => ((string?)t["content"] ?? "", (string?)t["status"] ?? "pending"))));
                break;

            case "permission.updated":
            case "permission.asked":
                if (Sid(p) != _sessionId) break;
                var pid = (string?)p["id"] ?? "";
                _permissions[pid] = true;
                var title = (string?)p["title"] ?? $"允许 {(string?)p["permission"] ?? (string?)p["type"] ?? "action"}";
                var patterns = p["patterns"] is JsonArray pa ? string.Join("\n", pa.Select(x => x?.ToString())) : (string?)p["pattern"]?.ToString();
                _sink.Emit(Ev.Approval(pid, title, patterns ?? p["metadata"]?.ToJsonString() ?? "", (string?)p["type"] ?? (string?)p["permission"] ?? "tool"));
                _sink.Emit(Ev.Status(SessionStates.Waiting, "需要批准：" + title));
                break;

            case "permission.replied":
                if (Sid(p) != _sessionId) break;
                var rid = (string?)p["permissionID"] ?? (string?)p["requestID"] ?? (string?)p["id"];
                if (rid is not null && _permissions.TryRemove(rid, out _))
                    _sink.Emit(Ev.ApprovalDone(rid, (string?)p["response"] ?? (string?)p["reply"] ?? "resolved"));
                break;
        }
    }

    private void OnPart(JsonObject part, string? delta)
    {
        var id = (string?)part["id"] ?? "";
        var role = _messageRoles.GetValueOrDefault((string?)part["messageID"] ?? "", "assistant");
        switch ((string?)part["type"])
        {
            case "text":
                if (role != "assistant") break;
                var text = (string?)part["text"] ?? "";
                if (text == _lastPrompt) break; // user echo before message.updated arrived
                if (delta is not null) _sink.Emit(Ev.Delta(id, delta));
                else _sink.Emit(Ev.Message(id, text));
                break;

            case "reasoning":
                if (delta is not null) _sink.Emit(Ev.Reasoning(id, delta, isDelta: true));
                else _sink.Emit(Ev.Reasoning(id, (string?)part["text"] ?? "", isDelta: false));
                break;

            case "tool":
                var state = part["state"] as JsonObject;
                var tool = (string?)part["tool"] ?? "tool";
                if (tool is "todowrite" or "todoread") break;
                var tid = (string?)part["callID"] ?? id;
                var status = (string?)state?["status"] switch
                {
                    "completed" => "ok",
                    "error" => "error",
                    _ => "running",
                };
                var title = (string?)state?["title"] is { Length: > 0 } t ? t : ToolTitles.For(tool, state?["input"]);
                var output = (string?)state?["output"] ?? (string?)state?["error"];
                _sink.Emit(Ev.Tool(tid, tool, title, status, ToolTitles.InputPreview(state?["input"]), output));
                if (status == "running" && _permissions.IsEmpty) _sink.Emit(Ev.Status(SessionStates.Running, title));
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _http?.Dispose();
        if (_proc is not null) await _proc.DisposeAsync();
    }
}
