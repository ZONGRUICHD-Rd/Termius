using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Conduit.Host.Core.Agents;
using Conduit.Host.Core.Protocol;

namespace Conduit.Host.Core.Sessions;

/// <summary>Creates, persists and drives sessions. Raises events consumed by connected phones and the desktop UI.</summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly AgentCatalog _catalog;
    private readonly string _dir;
    private readonly Channel<(string Id, string Line)> _persist = Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Timer _metaTimer;
    private readonly Task _persistLoop;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startLocks = new();

    public event Action<string, LoggedEvent>? SessionEvent;
    public event Action<JsonObject>? SummaryChanged;
    public event Action<string>? SessionRemoved;

    public SessionManager(AgentCatalog catalog, string dataDirectory)
    {
        _catalog = catalog;
        _dir = Path.Combine(dataDirectory, "sessions");
        Directory.CreateDirectory(_dir);
        LoadAll();
        _metaTimer = new Timer(_ => SaveDirtyMeta(), null, 2000, 2000);
        _persistLoop = Task.Run(PersistLoop);
    }

    public IEnumerable<Session> All => _sessions.Values;

    public Session? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    public JsonArray Summaries() =>
        new(_sessions.Values.Select(s => s.Summary()).OrderByDescending(s => (long?)s["updatedAt"]).Select(s => (JsonNode)s).ToArray());

    public async Task<Session> CreateAsync(string agentId, string cwd, string? title, string? model, CancellationToken ct)
    {
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException($"Folder not found: {cwd}");
        var now = Session.Now();
        var meta = new SessionMeta
        {
            Id = Guid.NewGuid().ToString("n")[..16],
            Agent = agentId,
            Cwd = cwd,
            Model = model,
            Title = string.IsNullOrWhiteSpace(title) ? $"New {agentId} session" : title!,
            TitleFromPrompt = string.IsNullOrWhiteSpace(title),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var session = Attach(new Session(meta));
        session.Dirty = true;
        SaveMeta(session);
        await EnsureStartedAsync(session, ct);
        return session;
    }

    private Session Attach(Session s)
    {
        s.EventAppended += (sess, e) =>
        {
            var kind = Ev.Kind(e.Ev);
            var transient = kind == Ev.KDelta || (kind == Ev.KReasoning && (bool?)e.Ev["delta"] == true);
            if (!transient) _persist.Writer.TryWrite((sess.Meta.Id, e.ToJson().ToJsonString()));
            SessionEvent?.Invoke(sess.Meta.Id, e);
        };
        s.SummaryChanged += sess => SummaryChanged?.Invoke(sess.Summary());
        _sessions[s.Meta.Id] = s;
        return s;
    }

    /// <summary>Starts (or resumes) the agent process if it isn't running.</summary>
    public async Task EnsureStartedAsync(Session s, CancellationToken ct)
    {
        var gate = _startLocks.GetOrAdd(s.Meta.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (s.Adapter is not null && s.State != SessionStates.Stopped) return;
            if (s.Adapter is not null)
            {
                s.Disposing = true;
                await s.Adapter.DisposeAsync();
                s.Adapter = null;
            }
            s.Disposing = false;
            s.Emit(Ev.Status(SessionStates.Starting, "Starting agent…"));
            try
            {
                var (adapter, path) = _catalog.Create(s.Meta.Agent);
                s.Adapter = adapter;
                await adapter.StartAsync(new AgentStartOptions(s.Meta.Id, s.Meta.Cwd, path, s.Meta.ResumeToken, s.Meta.Model), s, ct);
            }
            catch (Exception ex)
            {
                s.Emit(Ev.Error(ex.Message));
                s.Emit(Ev.Status(SessionStates.Error, Text.FirstLine(ex.Message)));
                if (s.Adapter is not null)
                {
                    s.Disposing = true;
                    await s.Adapter.DisposeAsync();
                    s.Adapter = null;
                }
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SendAsync(string id, string text, CancellationToken ct)
    {
        var s = Get(id) ?? throw new KeyNotFoundException("Session not found");
        s.Emit(Ev.User(text));
        await EnsureStartedAsync(s, ct);
        await s.Adapter!.SendAsync(text, ct);
    }

    public async Task InterruptAsync(string id, CancellationToken ct)
    {
        var s = Get(id) ?? throw new KeyNotFoundException("Session not found");
        if (s.Adapter is not null) await s.Adapter.InterruptAsync(ct);
    }

    public async Task RespondApprovalAsync(string id, string approvalId, string decision, CancellationToken ct)
    {
        var s = Get(id) ?? throw new KeyNotFoundException("Session not found");
        if (s.Adapter is null) throw new InvalidOperationException("Agent is not running");
        await s.Adapter.RespondApprovalAsync(approvalId, decision, ct);
    }

    public async Task StopAsync(string id)
    {
        var s = Get(id) ?? throw new KeyNotFoundException("Session not found");
        if (s.Adapter is null) return;
        s.Disposing = true;
        await s.Adapter.DisposeAsync();
        s.Adapter = null;
        s.Disposing = false;
        s.Emit(Ev.Status(SessionStates.Stopped, "Stopped"));
    }

    public async Task DeleteAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var s)) return;
        s.Disposing = true;
        if (s.Adapter is not null) await s.Adapter.DisposeAsync();
        try { Directory.Delete(Path.Combine(_dir, id), recursive: true); } catch (IOException) { }
        SessionRemoved?.Invoke(id);
    }

    public void Rename(string id, string title)
    {
        var s = Get(id) ?? throw new KeyNotFoundException("Session not found");
        s.Meta.Title = title;
        s.Meta.TitleFromPrompt = false;
        s.Dirty = true;
        SummaryChanged?.Invoke(s.Summary());
    }

    // ---------- persistence ----------

    private void LoadAll()
    {
        foreach (var dir in Directory.GetDirectories(_dir))
        {
            try
            {
                var metaPath = Path.Combine(dir, "meta.json");
                if (!File.Exists(metaPath)) continue;
                var meta = JsonSerializer.Deserialize(File.ReadAllText(metaPath), SessionJson.Default.SessionMeta);
                if (meta is null) continue;
                var s = new Session(meta);
                var eventsPath = Path.Combine(dir, "events.jsonl");
                if (File.Exists(eventsPath))
                {
                    var events = File.ReadLines(eventsPath)
                        .Select(l => { try { return LoggedEvent.FromJson(JsonNode.Parse(l)); } catch (JsonException) { return null; } })
                        .Where(e => e is not null)
                        .Select(e => e!)
                        .ToList();
                    s.Restore(events);
                    // Rewrite compacted.
                    var sb = new StringBuilder();
                    foreach (var e in s.Since(0)) sb.Append(e.ToJson().ToJsonString()).Append('\n');
                    File.WriteAllText(eventsPath, sb.ToString());
                }
                Attach(s);
            }
            catch (Exception)
            {
                // Skip unreadable sessions rather than failing startup.
            }
        }
    }

    private async Task PersistLoop()
    {
        await foreach (var (id, line) in _persist.Reader.ReadAllAsync())
        {
            try
            {
                var dir = Path.Combine(_dir, id);
                if (!_sessions.ContainsKey(id)) continue;
                Directory.CreateDirectory(dir);
                await File.AppendAllTextAsync(Path.Combine(dir, "events.jsonl"), line + "\n");
            }
            catch (IOException) { }
        }
    }

    private void SaveDirtyMeta()
    {
        foreach (var s in _sessions.Values.Where(s => s.Dirty)) SaveMeta(s);
    }

    private void SaveMeta(Session s)
    {
        try
        {
            s.Dirty = false;
            var dir = Path.Combine(_dir, s.Meta.Id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(s.Meta, SessionJson.Default.SessionMeta));
        }
        catch (IOException) { s.Dirty = true; }
    }

    public async ValueTask DisposeAsync()
    {
        await _metaTimer.DisposeAsync();
        SaveDirtyMeta();
        foreach (var s in _sessions.Values)
        {
            s.Disposing = true;
            if (s.Adapter is not null) await s.Adapter.DisposeAsync();
        }
        _persist.Writer.TryComplete();
        await Task.WhenAny(_persistLoop, Task.Delay(2000));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionMeta))]
internal partial class SessionJson : JsonSerializerContext;
