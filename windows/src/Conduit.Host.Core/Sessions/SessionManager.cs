using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Conduit.Host.Core.Agents;
using Conduit.Host.Core.Git;
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
        foreach (var loaded in _sessions.Values) ScheduleGitRefresh(loaded);
        _metaTimer = new Timer(_ => SaveDirtyMeta(), null, 2000, 2000);
        _persistLoop = Task.Run(PersistLoop);
    }

    public IEnumerable<Session> All => _sessions.Values;

    public Session? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    public JsonArray Summaries() =>
        new(_sessions.Values.Select(s => s.Summary()).OrderByDescending(s => (long?)s["updatedAt"]).Select(s => (JsonNode)s).ToArray());

    public async Task<Session> CreateAsync(string agentId, string cwd, string? title, string? model, string? mode, CancellationToken ct)
    {
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException($"文件夹不存在：{cwd}");
        var now = Session.Now();
        var meta = new SessionMeta
        {
            Id = Guid.NewGuid().ToString("n")[..16],
            Agent = agentId,
            Cwd = cwd,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            Mode = string.IsNullOrWhiteSpace(mode) ? null : mode,
            Title = string.IsNullOrWhiteSpace(title) ? "新对话" : title!,
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
        s.SummaryChanged += sess =>
        {
            var summary = sess.Summary();
            SummaryChanged?.Invoke(summary);
            if (!SessionStates.IsActive((string?)summary["state"] ?? "")) ScheduleGitRefresh(sess);
        };
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
            s.Emit(Ev.Status(SessionStates.Starting, "正在启动 Agent…"));
            try
            {
                var (adapter, path) = _catalog.Create(s.Meta.Agent);
                s.Adapter = adapter;
                await adapter.StartAsync(new AgentStartOptions(s.Meta.Id, s.Meta.Cwd, path, s.Meta.ResumeToken, s.Meta.Model, s.Meta.Mode), s, ct);
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

    public async Task SendAsync(string id, string text, IReadOnlyList<ImageInput> images, CancellationToken ct)
    {
        var s = Get(id) ?? throw new KeyNotFoundException("会话不存在");
        s.Emit(Ev.User(text, images.Count));
        await EnsureStartedAsync(s, ct);
        await s.Adapter!.SendAsync(text, images, ct);
    }

    /// <summary>Switch model and/or permission mode. Applied live where the agent supports it, else on restart.</summary>
    public async Task SetOptionsAsync(string id, string? model, string? mode, CancellationToken ct)
    {
        var s = Get(id) ?? throw new KeyNotFoundException("会话不存在");
        if (model is not null) s.Meta.Model = model.Length == 0 ? null : model;
        if (mode is not null) s.Meta.Mode = mode.Length == 0 ? null : mode;
        s.Dirty = true;
        if (s.Adapter is not null && !await s.Adapter.UpdateOptionsAsync(model, mode, ct) && !SessionStates.IsActive(s.State))
        {
            await StopAsync(id);
            await EnsureStartedAsync(s, ct);
        }
        s.Emit(Ev.Notice(DescribeOptions(s.Meta)));
    }

    private string DescribeOptions(SessionMeta m)
    {
        var mode = AgentCatalog.ModesFor(m.Agent).FirstOrDefault(x => x.Id == m.Mode)?.Label ?? "默认";
        return $"模型：{m.Model ?? "默认"} · 模式：{mode}";
    }

    /// <summary>Creates a session that continues a conversation started on this PC (e.g. in Claude Desktop or the Codex app).</summary>
    public Session Import(string agentId, string resumeToken, string cwd, string title, IEnumerable<JsonObject> history)
    {
        var existing = _sessions.Values.FirstOrDefault(x => x.Meta.Agent == agentId && x.Meta.ResumeToken == resumeToken);
        if (existing is not null) return existing;
        var now = Session.Now();
        var meta = new SessionMeta
        {
            Id = Guid.NewGuid().ToString("n")[..16],
            Agent = agentId,
            Cwd = cwd,
            Title = string.IsNullOrWhiteSpace(title) ? "导入的对话" : title,
            TitleFromPrompt = false,
            ResumeToken = resumeToken,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var s = Attach(new Session(meta));
        foreach (var ev in history) s.Emit(ev);
        s.Emit(Ev.Notice("已从电脑导入此对话，发送消息即可继续。"));
        s.Emit(Ev.Status(SessionStates.Stopped));
        s.Dirty = true;
        SaveMeta(s);
        return s;
    }

    private readonly ConcurrentDictionary<string, int> _gitRefreshVersion = new();

    private void ScheduleGitRefresh(Session s)
    {
        var version = _gitRefreshVersion.AddOrUpdate(s.Meta.Id, 1, (_, v) => v + 1);
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            if (_gitRefreshVersion.GetValueOrDefault(s.Meta.Id) != version) return;
            if (!Directory.Exists(s.Meta.Cwd)) return;
            s.SetGitStats(await GitService.QuickStatsAsync(s.Meta.Cwd, CancellationToken.None));
        });
    }

    public void RefreshGitStats(string id)
    {
        if (Get(id) is { } s) ScheduleGitRefresh(s);
    }

    public async Task InterruptAsync(string id, CancellationToken ct)
    {
        var s = Get(id) ?? throw new KeyNotFoundException("会话不存在");
        if (s.Adapter is not null) await s.Adapter.InterruptAsync(ct);
    }

    public async Task RespondApprovalAsync(string id, string approvalId, string decision, CancellationToken ct)
    {
        var s = Get(id) ?? throw new KeyNotFoundException("会话不存在");
        if (s.Adapter is null) throw new InvalidOperationException("Agent 未运行");
        await s.Adapter.RespondApprovalAsync(approvalId, decision, ct);
    }

    public async Task StopAsync(string id)
    {
        var s = Get(id) ?? throw new KeyNotFoundException("会话不存在");
        if (s.Adapter is null) return;
        s.Disposing = true;
        await s.Adapter.DisposeAsync();
        s.Adapter = null;
        s.Disposing = false;
        s.Emit(Ev.Status(SessionStates.Stopped, "已停止"));
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
        var s = Get(id) ?? throw new KeyNotFoundException("会话不存在");
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
