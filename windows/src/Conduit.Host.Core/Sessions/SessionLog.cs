using System.Text.Json.Nodes;
using Conduit.Host.Core.Protocol;

namespace Conduit.Host.Core.Sessions;

public sealed record LoggedEvent(long Seq, long Ts, JsonObject Ev)
{
    public JsonObject ToJson() => new() { ["seq"] = Seq, ["ts"] = Ts, ["ev"] = Ev.DeepClone() };

    public static LoggedEvent? FromJson(JsonNode? n) =>
        n is JsonObject o && o["ev"] is JsonObject ev ? new LoggedEvent((long?)o["seq"] ?? 0, (long?)o["ts"] ?? 0, (JsonObject)ev.DeepClone()) : null;
}

/// <summary>
/// Append-only event log with compaction so a reconnecting phone can catch up cheaply:
///  * streamed deltas are dropped once the authoritative message arrives,
///  * tool updates are merged into a single entry per tool call,
///  * only the latest plan / status is kept.
/// Sequence numbers are never reused, so "give me everything after seq N" stays correct.
/// </summary>
public sealed class SessionLog
{
    private readonly LinkedList<LoggedEvent> _events = new();
    private readonly Dictionary<string, List<LinkedListNode<LoggedEvent>>> _index = [];
    private readonly int _capacity;

    public long LastSeq { get; private set; }
    public int Count => _events.Count;

    public SessionLog(int capacity = 5000) => _capacity = capacity;

    /// <summary>Appends <paramref name="ev"/> (after merging with what it supersedes) and returns the stored entry.</summary>
    public LoggedEvent Append(JsonObject ev, long ts) => AppendCore(ev, ts, null);

    /// <summary>Restores a persisted entry keeping its original seq, so clients' cursors survive host restarts.</summary>
    public void Restore(LoggedEvent e) => AppendCore(e.Ev, e.Ts, e.Seq);

    private LoggedEvent AppendCore(JsonObject ev, long ts, long? seq)
    {
        var kind = Ev.Kind(ev);
        string? key = null;
        switch (kind)
        {
            case Ev.KDelta:
                key = "d:" + ev["mid"];
                break;
            case Ev.KMessage:
                Remove("d:" + ev["mid"]);
                Remove("m:" + ev["mid"]);
                key = "m:" + ev["mid"];
                break;
            case Ev.KReasoning:
                if ((bool?)ev["delta"] == true) key = "rd:" + ev["mid"];
                else
                {
                    Remove("rd:" + ev["mid"]);
                    Remove("r:" + ev["mid"]);
                    key = "r:" + ev["mid"];
                }
                break;
            case Ev.KTool:
                key = "t:" + ev["tid"];
                if (_index.TryGetValue(key, out var prev) && prev.LastOrDefault(n => n.List is not null) is { } last)
                    ev = Merge(last.Value.Ev, ev);
                Remove(key);
                break;
            case Ev.KPlan:
                key = "plan";
                Remove(key);
                break;
            case Ev.KStatus:
                key = "status";
                Remove(key);
                break;
        }

        LastSeq = seq is { } fixedSeq ? Math.Max(LastSeq, fixedSeq) : LastSeq + 1;
        var entry = new LoggedEvent(seq ?? LastSeq, ts, ev);
        var node = _events.AddLast(entry);
        if (key is not null)
        {
            if (!_index.TryGetValue(key, out var list)) _index[key] = list = [];
            list.Add(node);
        }
        while (_events.Count > _capacity) _events.RemoveFirst();
        return entry;
    }

    public List<LoggedEvent> Since(long seq, int max = 2000)
    {
        var result = new List<LoggedEvent>();
        for (var n = _events.Last; n is not null && n.Value.Seq > seq; n = n.Previous) result.Add(n.Value);
        result.Reverse();
        return result.Count > max ? result[^max..] : result;
    }

    public JsonObject? Find(string key) =>
        _index.TryGetValue(key, out var l) ? l.LastOrDefault(n => n.List is not null)?.Value.Ev : null;

    private void Remove(string key)
    {
        if (!_index.Remove(key, out var nodes)) return;
        foreach (var n in nodes)
            if (n.List is not null) _events.Remove(n);
    }

    internal static JsonObject Merge(JsonObject older, JsonObject newer)
    {
        var merged = (JsonObject)older.DeepClone();
        foreach (var (k, v) in newer)
        {
            if (v is null) continue;
            if (v is JsonValue s && s.TryGetValue<string>(out var str) && str.Length == 0 && merged[k] is not null) continue;
            merged[k] = v.DeepClone();
        }
        return merged;
    }
}
