using System.Text.Json.Nodes;
using Conduit.Host.Core.Agents;
using Conduit.Host.Core.Git;
using Conduit.Host.Core.Protocol;
using Xunit;

namespace Conduit.Host.Core.Tests;

public class GitServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "conduit-git-" + Guid.NewGuid().ToString("n"));

    public GitServiceTests()
    {
        Directory.CreateDirectory(_dir);
        Run("init", "-q", "-b", "main");
        Run("config", "user.email", "t@example.com");
        Run("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "one\ntwo\n");
        Run("add", "-A");
        Run("commit", "-q", "-m", "init");
    }

    private void Run(params string[] args) => Assert.True(GitService.RunAsync("git", _dir, args, CancellationToken.None).Result.Ok);

    [Fact]
    public async Task StatusDiffCommit()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "one\nTWO\nthree\n");
        File.WriteAllText(Path.Combine(_dir, "new.txt"), "x\ny\n");

        var st = await GitService.StatusAsync(_dir, CancellationToken.None);
        Assert.True((bool?)st["isRepo"]);
        Assert.Equal("main", (string?)st["branch"]);
        var files = (JsonArray)st["files"]!;
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => (string?)f!["path"] == "new.txt" && (string?)f["status"] == "added" && (int?)f["added"] == 2);
        Assert.Contains(files, f => (string?)f!["path"] == "a.txt" && (int?)f["added"] == 2 && (int?)f["deleted"] == 1);

        var diff = (string)(await GitService.DiffAsync(_dir, null, CancellationToken.None))["diff"]!;
        Assert.Contains("+TWO", diff);
        Assert.Contains("new.txt", diff);

        var commit = await GitService.CommitAsync(_dir, "change", CancellationToken.None);
        Assert.False(string.IsNullOrEmpty((string?)commit["sha"]));
        var after = await GitService.StatusAsync(_dir, CancellationToken.None);
        Assert.Empty((JsonArray)after["files"]!);
    }

    [Fact]
    public async Task NotARepo()
    {
        var plain = Path.Combine(Path.GetTempPath(), "conduit-plain-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(plain);
        var st = await GitService.StatusAsync(plain, CancellationToken.None);
        Assert.False((bool?)st["isRepo"]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }
}

public class HistoryTests
{
    [Fact]
    public void ImportsClaudeAndCodexTranscripts()
    {
        var home = Path.Combine(Path.GetTempPath(), "conduit-home-" + Guid.NewGuid().ToString("n"));
        var claudeDir = Path.Combine(home, ".claude", "projects", "C--proj");
        Directory.CreateDirectory(claudeDir);
        File.WriteAllLines(Path.Combine(claudeDir, "11111111-aaaa.jsonl"),
        [
            """{"type":"user","cwd":"C:\\proj","message":{"role":"user","content":"Fix the login bug"}}""",
            """{"type":"assistant","message":{"id":"msg_1","content":[{"type":"text","text":"Looking."},{"type":"tool_use","id":"tu1","name":"Read","input":{"file_path":"login.ts"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu1","content":"code"}]}}""",
        ]);
        var codexDir = Path.Combine(home, ".codex", "sessions", "2026", "10", "10");
        Directory.CreateDirectory(codexDir);
        File.WriteAllLines(Path.Combine(codexDir, "rollout-2026-10-10T00-00-00-thr-42.jsonl"),
        [
            """{"type":"session_meta","payload":{"id":"thr-42","cwd":"/work/app"}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>x</environment_context>"}]}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Add tests"}]}}""",
            """{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Done."}]}}""",
        ]);

        History.HomeOverride = home;
        try
        {
            var claude = History.List(AgentCatalog.Claude);
            var c = Assert.Single(claude);
            Assert.Equal("11111111-aaaa", c.Id);
            Assert.Equal("Fix the login bug", c.Title);
            var events = History.LoadEvents(c);
            Assert.Equal(Ev.KUser, Ev.Kind(events[0]));
            Assert.Contains(events, e => Ev.Kind(e) == Ev.KTool && (string?)e["title"] == "Read login.ts");

            var codex = Assert.Single(History.List(AgentCatalog.Codex));
            Assert.Equal("thr-42", codex.Id);
            Assert.Equal("Add tests", codex.Title);
            Assert.Equal("/work/app", codex.Cwd);
            Assert.Equal(2, History.LoadEvents(codex).Count);
        }
        finally
        {
            History.HomeOverride = null;
            Directory.Delete(home, true);
        }
    }
}
