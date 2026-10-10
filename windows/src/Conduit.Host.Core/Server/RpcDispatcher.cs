using System.Text.Json.Nodes;
using Conduit.Host.Core.Agents;
using Conduit.Host.Core.Git;
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
        string S(string key) => (string?)p[key] ?? throw new ArgumentException($"缺少参数 {key}");
        string Cwd() => (string?)p["cwd"] is { Length: > 0 } c ? c
            : host.Sessions.Get(S("sessionId"))?.Meta.Cwd ?? throw new KeyNotFoundException("会话不存在");
        List<ImageInput> Images() => (p["images"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(i => new ImageInput((string?)i["mimeType"] ?? "image/png", (string?)i["data"] ?? ""))
            .Where(i => i.Base64.Length > 0)
            .Take(8)
            .ToList();
        void RefreshStats()
        {
            if ((string?)p["sessionId"] is { } sid) host.Sessions.RefreshGitStats(sid);
        }

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
                    var s = await host.Sessions.CreateAsync(S("agent"), S("cwd"), (string?)p["title"], (string?)p["model"], (string?)p["mode"], ct);
                    host.RememberDirectory(s.Meta.Cwd);
                    var images = Images();
                    if ((string?)p["prompt"] is { Length: > 0 } prompt || images.Count > 0)
                        await host.Sessions.SendAsync(s.Meta.Id, (string?)p["prompt"] ?? "", images, ct);
                    conn.SendResult(id, new JsonObject { ["session"] = s.Summary() });
                    break;
                }

                case "session.send":
                    await host.Sessions.SendAsync(S("sessionId"), (string?)p["text"] ?? "", Images(), ct);
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
                    var s = host.Sessions.Get(S("sessionId")) ?? throw new KeyNotFoundException("会话不存在");
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
                        throw new ArgumentException("无效的审批选项");
                    await host.Sessions.RespondApprovalAsync(S("sessionId"), S("approvalId"), decision, ct);
                    conn.SendResult(id, new JsonObject());
                    break;
                }

                case "session.setOptions":
                    await host.Sessions.SetOptionsAsync(S("sessionId"), (string?)p["model"], (string?)p["mode"], ct);
                    conn.SendResult(id, new JsonObject { ["session"] = host.Sessions.Get(S("sessionId"))!.Summary() });
                    break;

                // ---- code changes (Codex / Claude Code mobile "review & ship") ----
                case "git.status":
                    conn.SendResult(id, await GitService.StatusAsync(Cwd(), ct));
                    break;
                case "git.diff":
                    conn.SendResult(id, await GitService.DiffAsync(Cwd(), (string?)p["path"], ct));
                    break;
                case "git.commit":
                    conn.SendResult(id, await GitService.CommitAsync(Cwd(), S("message"), ct));
                    RefreshStats();
                    break;
                case "git.push":
                    conn.SendResult(id, await GitService.PushAsync(Cwd(), ct));
                    RefreshStats();
                    break;
                case "git.branch":
                    conn.SendResult(id, await GitService.CreateBranchAsync(Cwd(), S("name"), ct));
                    RefreshStats();
                    break;
                case "git.pr":
                    conn.SendResult(id, await GitService.CreatePullRequestAsync(Cwd(), S("title"), (string?)p["body"] ?? "", (bool?)p["draft"] == true, ct));
                    break;
                case "git.discard":
                    conn.SendResult(id, await GitService.DiscardFileAsync(Cwd(), S("path"), ct));
                    RefreshStats();
                    break;

                // ---- conversations started on the PC ----
                case "history.list":
                {
                    var agent = S("agent");
                    var items = History.List(agent).Select(i => (JsonNode)new JsonObject
                    {
                        ["id"] = i.Id,
                        ["title"] = i.Title,
                        ["cwd"] = i.Cwd,
                        ["updatedAt"] = i.UpdatedAt.ToUnixTimeMilliseconds(),
                        ["imported"] = host.Sessions.All.Any(x => x.Meta.Agent == agent && x.Meta.ResumeToken == i.Id),
                    }).ToArray();
                    conn.SendResult(id, new JsonObject { ["supported"] = History.Supports(agent), ["items"] = new JsonArray(items) });
                    break;
                }
                case "history.import":
                {
                    var item = History.Find(S("agent"), S("id")) ?? throw new KeyNotFoundException("找不到这条对话记录");
                    var cwd = Directory.Exists(item.Cwd) ? item.Cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var s = host.Sessions.Import(item.Agent, item.Id, cwd, item.Title, History.LoadEvents(item));
                    conn.SendResult(id, new JsonObject { ["session"] = s.Summary() });
                    break;
                }

                // ---- connection settings (also editable from the phone) ----
                case "host.setConnection":
                    host.ApplyConnectionSettings(p);
                    conn.SendResult(id, host.Info());
                    break;

                case "fs.list":
                    conn.SendResult(id, FileBrowser.List((string?)p["path"], host.Config.RecentDirectories));
                    break;

                case "push.register":
                    host.RegisterPush(conn.Device!.Id, (string?)p["token"], (string?)p["env"], (string?)p["bundleId"]);
                    conn.SendResult(id, new JsonObject { ["apns"] = host.Config.Apns.Enabled });
                    break;

                default:
                    conn.SendError(id, $"不支持的请求：{method}（请更新电脑端）", "unknown_method");
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException ex)
        {
            conn.SendError(id, ex.Message, "timeout");
        }
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
