using System.Text.Json.Nodes;
using Conduit.Host.Core.Protocol;
using Conduit.Host.Core.Sessions;

namespace Conduit.Host.Core.Server;

/// <summary>Request handlers for the encrypted application protocol (docs/PROTOCOL.md §3).</summary>
public sealed class RpcDispatcher(HostRuntime host)
{
    public async Task HandleAsync(ClientConnection conn, JsonObject req, CancellationToken ct)
    {
        var id = req["id"];
        var method = (string?)req["m"] ?? "";
        var p = req["p"] as JsonObject ?? new JsonObject();
        string S(string key) => (string?)p[key] ?? throw new ArgumentException($"Missing '{key}'");

        try
        {
            switch (method)
            {
                case "ping":
                    conn.SendResult(id, new JsonObject { ["now"] = Session.Now() });
                    break;

                case "host.info":
                    conn.SendResult(id, host.Info());
                    break;

                case "agents.refresh":
                    await host.Agents.RefreshAsync();
                    conn.SendResult(id, host.Info());
                    break;

                case "sessions.list":
                    conn.SendResult(id, new JsonObject { ["sessions"] = host.Sessions.Summaries() });
                    break;

                case "session.create":
                {
                    var s = await host.Sessions.CreateAsync(S("agent"), S("cwd"), (string?)p["title"], (string?)p["model"], ct);
                    host.RememberDirectory(s.Meta.Cwd);
                    if ((string?)p["prompt"] is { Length: > 0 } prompt) await host.Sessions.SendAsync(s.Meta.Id, prompt, ct);
                    conn.SendResult(id, new JsonObject { ["session"] = s.Summary() });
                    break;
                }

                case "session.send":
                    await host.Sessions.SendAsync(S("sessionId"), S("text"), ct);
                    conn.SendResult(id, new JsonObject());
                    break;

                case "session.interrupt":
                    await host.Sessions.InterruptAsync(S("sessionId"), ct);
                    conn.SendResult(id, new JsonObject());
                    break;

                case "session.stop":
                    await host.Sessions.StopAsync(S("sessionId"));
                    conn.SendResult(id, new JsonObject());
                    break;

                case "session.delete":
                    await host.Sessions.DeleteAsync(S("sessionId"));
                    conn.SendResult(id, new JsonObject());
                    break;

                case "session.rename":
                    host.Sessions.Rename(S("sessionId"), S("title"));
                    conn.SendResult(id, new JsonObject());
                    break;

                case "session.subscribe":
                {
                    var s = host.Sessions.Get(S("sessionId")) ?? throw new KeyNotFoundException("Session not found");
                    var since = (long?)p["since"] ?? 0;
                    // Atomic w.r.t. the session's event stream: every event after the backfill is delivered live,
                    // and always after this response (both go through the same ordered outbox).
                    s.Atomically(() =>
                    {
                        var events = new JsonArray(s.Since(since).Select(e => (JsonNode)e.ToJson()).ToArray());
                        conn.Subscribe(s.Meta.Id);
                        conn.SendResult(id, new JsonObject { ["events"] = events, ["lastSeq"] = s.LastSeq, ["session"] = s.Summary() });
                    });
                    break;
                }

                case "session.unsubscribe":
                    conn.Unsubscribe(S("sessionId"));
                    conn.SendResult(id, new JsonObject());
                    break;

                case "approval.respond":
                {
                    var decision = S("decision");
                    if (decision is not (ApprovalDecisions.Allow or ApprovalDecisions.AllowAlways or ApprovalDecisions.Deny))
                        throw new ArgumentException("Invalid decision");
                    await host.Sessions.RespondApprovalAsync(S("sessionId"), S("approvalId"), decision, ct);
                    conn.SendResult(id, new JsonObject());
                    break;
                }

                case "fs.list":
                    conn.SendResult(id, FileBrowser.List((string?)p["path"], host.Config.RecentDirectories));
                    break;

                case "push.register":
                    host.RegisterPush(conn.Device!.Id, (string?)p["token"], (string?)p["env"], (string?)p["bundleId"]);
                    conn.SendResult(id, new JsonObject { ["apns"] = host.Config.Apns.Enabled });
                    break;

                default:
                    conn.SendError(id, $"Unknown method '{method}'", "unknown_method");
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            conn.SendError(id, ex.Message, ex switch
            {
                KeyNotFoundException => "not_found",
                ArgumentException => "bad_request",
                DirectoryNotFoundException => "not_found",
                _ => "error",
            });
        }
    }
}
