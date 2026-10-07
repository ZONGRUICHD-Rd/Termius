using Conduit.Host.Core.Protocol;
using Conduit.Host.Core.Sessions;
using Xunit;

namespace Conduit.Host.Core.Tests;

public class SessionLogTests
{
    [Fact]
    public void FinalMessageReplacesDeltas()
    {
        var log = new SessionLog();
        log.Append(Ev.User("hi"), 1);
        log.Append(Ev.Delta("m1", "Hel"), 2);
        log.Append(Ev.Delta("m1", "lo"), 3);
        log.Append(Ev.Message("m1", "Hello"), 4);

        var all = log.Since(0);
        Assert.Equal(2, all.Count);
        Assert.Equal(Ev.KMessage, Ev.Kind(all[1].Ev));
        Assert.Equal(4, all[1].Seq);
        Assert.Equal(4, log.LastSeq);
    }

    [Fact]
    public void ToolUpdatesMergeKeepingTitle()
    {
        var log = new SessionLog();
        log.Append(Ev.Tool("t1", "Bash", "Run ls", "running", "ls"), 1);
        var done = Ev.Tool("t1", "Bash", "", "ok", null, "a.txt");
        done.Remove("title");
        log.Append(done, 2);

        var all = log.Since(0);
        Assert.Single(all);
        Assert.Equal("Run ls", (string?)all[0].Ev["title"]);
        Assert.Equal("ok", (string?)all[0].Ev["status"]);
        Assert.Equal("a.txt", (string?)all[0].Ev["output"]);
        Assert.Equal("ls", (string?)all[0].Ev["input"]);
    }

    [Fact]
    public void SinceReturnsOnlyNewerEvents()
    {
        var log = new SessionLog();
        for (var i = 0; i < 10; i++) log.Append(Ev.Notice($"n{i}"), i);
        var tail = log.Since(7);
        Assert.Equal([8L, 9L, 10L], tail.Select(e => e.Seq).ToArray());
    }

    [Fact]
    public void RestoreKeepsOriginalSequence()
    {
        var log = new SessionLog();
        log.Restore(new LoggedEvent(5, 1, Ev.User("a")));
        log.Restore(new LoggedEvent(9, 2, Ev.Message("m", "b")));
        Assert.Equal(9, log.LastSeq);
        Assert.Equal(10, log.Append(Ev.Notice("c"), 3).Seq);
    }

    [Fact]
    public void SessionSummaryTracksPlanAndApprovals()
    {
        var s = new Session(new SessionMeta { Id = "s1", Agent = "demo", Title = "x", TitleFromPrompt = true });
        s.Emit(Ev.User("Fix the login bug please"));
        s.Emit(Ev.Status(SessionStates.Running, "Thinking"));
        s.Emit(Ev.Plan([("a", "completed"), ("b", "in_progress"), ("c", "pending")]));
        s.Emit(Ev.Approval("a1", "Run rm", "rm -rf build", "command"));
        s.Emit(Ev.Status(SessionStates.Running, "Run rm"));
        var sum = s.Summary();
        Assert.Equal("Fix the login bug please", (string?)sum["title"]);
        Assert.Equal(SessionStates.Waiting, (string?)sum["state"]);
        Assert.Equal(1, (int?)sum["done"]);
        Assert.Equal(3, (int?)sum["total"]);
        Assert.Equal(1, (int?)sum["approvals"]);

        s.Emit(Ev.ApprovalDone("a1", "allow"));
        Assert.Equal(SessionStates.Running, (string?)s.Summary()["state"]);
    }

    [Fact]
    public async Task DeltasAreCoalesced()
    {
        var s = new Session(new SessionMeta { Id = "s1" });
        var seen = new List<LoggedEvent>();
        s.EventAppended += (_, e) => { lock (seen) seen.Add(e); };
        for (var i = 0; i < 50; i++) s.Emit(Ev.Delta("m", "x"));
        await Task.Delay(Session.DeltaCoalesceMs * 4);
        lock (seen)
        {
            Assert.True(seen.Count < 5, $"expected coalescing, got {seen.Count} frames");
            Assert.Equal(50, seen.Sum(e => ((string?)e.Ev["text"])!.Length));
        }
    }
}
