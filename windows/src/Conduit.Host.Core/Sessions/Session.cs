using System.Text;
using System.Text.Json.Nodes;
using Conduit.Host.Core.Agents;
using Conduit.Host.Core.Protocol;

namespace Conduit.Host.Core.Sessions;

public sealed class SessionMeta
{
    public string Id { get; set; } = "";
    public string Agent { get; set; } = "";
    public string Title { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string? Model { get; set; }
    public string? ResumeToken { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public bool TitleFromPrompt { get; set; } = true;
}

/// <summary>
/// One agent conversation: owns the adapter, the compacted event log and the live summary that feeds
/// the phone's session list and Dynamic Island. Thread-safe; events are emitted in seq order.
/// </summary>
public sealed class Session : IAgentSink
{
    public const int DeltaCoalesceMs = 33;

    private readonly object _lock = new();
    private readonly SessionLog _log = new();
    private readonly Timer _flushTimer;
    private string? _pendingKind;
    private string? _pendingMid;
    private readonly StringBuilder _pending = new();
    private readonly HashSet<string> _openApprovals = [];
    private string _state = SessionStates.Stopped;
    private string? _detail;
    private int _done, _total;
    private long _turnStartedAt, _turnEndedAt;
    private string _preview = "";

    public SessionMeta Meta { get; }
    public IAgentAdapter? Adapter { get; set; }
    public bool Disposing { get; set; }
    public bool Dirty { get; set; }
    public string State { get { lock (_lock) return _state; } }

    /// <summary>Raised under the session lock, in seq order. Handlers must not block.</summary>
    public event Action<Session, LoggedEvent>? EventAppended;
    public event Action<Session>? SummaryChanged;

    public Session(SessionMeta meta)
    {
        Meta = meta;
        _flushTimer = new Timer(_ => { lock (_lock) FlushPending(); }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Emit(JsonObject ev)
    {
        lock (_lock)
        {
            var kind = Ev.Kind(ev);
            var isDelta = kind == Ev.KDelta || (kind == Ev.KReasoning && (bool?)ev["delta"] == true);
            if (isDelta)
            {
                var mid = (string?)ev["mid"] ?? "";
                if (_pendingKind != kind || _pendingMid != mid) FlushPending();
                _pendingKind = kind;
                _pendingMid = mid;
                _pending.Append((string?)ev["text"]);
                if (_pending.Length > 0) _flushTimer.Change(DeltaCoalesceMs, Timeout.Infinite);
                return;
            }
            FlushPending();
            AppendLocked(ev);
        }
    }

    private void FlushPending()
    {
        if (_pendingKind is null || _pending.Length == 0)
        {
            _pendingKind = null;
            return;
        }
        var ev = _pendingKind == Ev.KDelta
            ? Ev.Delta(_pendingMid!, _pending.ToString())
            : Ev.Reasoning(_pendingMid!, _pending.ToString(), isDelta: true);
        _pending.Clear();
        _pendingKind = null;
        AppendLocked(ev);
    }

    private void AppendLocked(JsonObject ev)
    {
        var summaryChanged = UpdateSummary(ev);
        var entry = _log.Append(ev, Now());
        Meta.UpdatedAt = entry.Ts;
        Dirty = true;
        EventAppended?.Invoke(this, entry);
        if (summaryChanged) SummaryChanged?.Invoke(this);
    }

    private bool UpdateSummary(JsonObject ev)
    {
        switch (Ev.Kind(ev))
        {
            case Ev.KStatus:
                var state = (string?)ev["state"] ?? _state;
                var detail = (string?)ev["detail"];
                if (SessionStates.IsActive(state) && !SessionStates.IsActive(_state))
                {
                    _turnStartedAt = Now();
                    _done = _total = 0;
                }
                if (!SessionStates.IsActive(state) && SessionStates.IsActive(_state)) _turnEndedAt = Now();
                if (_openApprovals.Count > 0 && state == SessionStates.Running) state = SessionStates.Waiting;
                var changed = state != _state || detail != _detail;
                _state = state;
                _detail = detail ?? (SessionStates.IsActive(state) ? _detail : null);
                return changed;

            case Ev.KTool when (string?)ev["status"] == "running" && (string?)ev["title"] is { Length: > 0 } title:
                _detail = title;
                return true;

            case Ev.KPlan when ev["items"] is JsonArray items:
                _total = items.Count;
                _done = items.Count(i => (string?)i?["status"] == "completed");
                var active = items.FirstOrDefault(i => (string?)i?["status"] == "in_progress");
                if (active is not null && _openApprovals.Count == 0) _detail = (string?)active["text"];
                return true;

            case Ev.KMessage:
                _preview = Text.FirstLine((string?)ev["text"] ?? "", 140);
                return true;

            case Ev.KUser:
                var text = (string?)ev["text"] ?? "";
                _preview = Text.FirstLine(text, 140);
                if (Meta.TitleFromPrompt)
                {
                    Meta.Title = Text.FirstLine(text, 60);
                    Meta.TitleFromPrompt = false;
                }
                return true;

            case Ev.KApproval:
                _openApprovals.Add((string?)ev["aid"] ?? "");
                return true;

            case Ev.KApprovalDone:
                _openApprovals.Remove((string?)ev["aid"] ?? "");
                if (_openApprovals.Count == 0 && _state == SessionStates.Waiting) _state = SessionStates.Running;
                return true;

            case Ev.KError:
                _preview = "⚠︎ " + Text.FirstLine((string?)ev["text"] ?? "", 140);
                return true;
        }
        return false;
    }

    public void SetResumeToken(string token)
    {
        lock (_lock)
        {
            if (Meta.ResumeToken == token) return;
            Meta.ResumeToken = token;
            Dirty = true;
        }
    }

    public void Exited(int? exitCode, string? reason)
    {
        if (Disposing) return;
        lock (_lock)
        {
            foreach (var aid in _openApprovals.ToList()) AppendLocked(Ev.ApprovalDone(aid, "cancelled"));
            if (reason is { Length: > 0 }) AppendLocked(Ev.Error($"Agent exited ({exitCode}): {Text.Clip(reason, 1500)}"));
            AppendLocked(Ev.Status(SessionStates.Stopped, exitCode is 0 or null ? null : $"Exited with code {exitCode}"));
        }
    }

    public List<LoggedEvent> Since(long seq)
    {
        lock (_lock)
        {
            FlushPending();
            return _log.Since(seq);
        }
    }

    public long LastSeq { get { lock (_lock) return _log.LastSeq; } }

    /// <summary>Runs <paramref name="action"/> while no event can be appended (the lock is re-entrant).</summary>
    public void Atomically(Action action)
    {
        lock (_lock)
        {
            FlushPending();
            action();
        }
    }

    public void Restore(IEnumerable<LoggedEvent> events)
    {
        lock (_lock)
        {
            foreach (var e in events)
            {
                UpdateSummary(e.Ev);
                _log.Restore(e);
            }
            // Processes don't survive a host restart.
            if (_state != SessionStates.Stopped)
                _state = SessionStates.Stopped;
            _openApprovals.Clear();
            _detail = null;
        }
    }

    public JsonObject Summary()
    {
        lock (_lock)
        {
            return new JsonObject
            {
                ["id"] = Meta.Id,
                ["agent"] = Meta.Agent,
                ["title"] = Meta.Title,
                ["cwd"] = Meta.Cwd,
                ["state"] = _state,
                ["detail"] = _detail,
                ["done"] = _done,
                ["total"] = _total,
                ["turnStartedAt"] = _turnStartedAt,
                ["turnEndedAt"] = _turnEndedAt,
                ["createdAt"] = Meta.CreatedAt,
                ["updatedAt"] = Meta.UpdatedAt,
                ["preview"] = _preview,
                ["approvals"] = _openApprovals.Count,
                ["lastSeq"] = _log.LastSeq,
            };
        }
    }
}
