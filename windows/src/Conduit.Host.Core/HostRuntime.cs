using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Conduit.Host.Core.Agents;
using Conduit.Host.Core.Push;
using Conduit.Host.Core.Relay;
using Conduit.Host.Core.Security;
using Conduit.Host.Core.Server;
using Conduit.Host.Core.Sessions;

namespace Conduit.Host.Core;

public static class HostInfo
{
    public static string Version => typeof(HostInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}

/// <summary>Composition root for the desktop host. The WinUI app and the CLI both just create and start one.</summary>
public sealed class HostRuntime : IAsyncDisposable
{
    public static readonly TimeSpan PairingTokenLifetime = TimeSpan.FromMinutes(30);

    private readonly object _configLock = new();
    private readonly ConcurrentDictionary<string, ClientConnection> _connections = new();
    private readonly ConcurrentQueue<string> _log = new();
    private RelayClient? _relay;
    private DirectServer? _direct;

    public string DataDirectory { get; }
    public string ConfigPath { get; }
    public HostConfig Config { get; }
    public HostIdentity Identity { get; }
    public AgentCatalog Agents { get; }
    public SessionManager Sessions { get; }
    public RpcDispatcher Dispatcher { get; }
    public ApnsLiveActivityPusher Pusher { get; }

    /// <summary>Something the UI shows changed (connections, devices, relay status, sessions).</summary>
    public event Action? Changed;
    public event Action<string>? LogAdded;

    public HostRuntime(string dataDirectory, ISecretProtector? protector = null)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        ConfigPath = Path.Combine(dataDirectory, "config.json");
        Config = HostConfig.Load(ConfigPath);
        Identity = HostIdentity.LoadOrCreate(Path.Combine(dataDirectory, "identity.json"), protector ?? new NoopProtector());
        if (string.IsNullOrEmpty(Config.PairingToken)) RegeneratePairingToken();
        Agents = new AgentCatalog(Config);
        Sessions = new SessionManager(Agents, dataDirectory);
        Dispatcher = new RpcDispatcher(this);
        Pusher = new ApnsLiveActivityPusher(this);

        Sessions.SessionEvent += (sessionId, e) =>
        {
            JsonObject? payload = null;
            foreach (var c in _connections.Values)
            {
                if (!c.IsSubscribed(sessionId)) continue;
                payload ??= new JsonObject { ["sessionId"] = sessionId, ["seq"] = e.Seq, ["ts"] = e.Ts, ["ev"] = e.Ev.DeepClone() };
                c.SendEvent("session.event", (JsonObject)payload.DeepClone());
            }
        };
        Sessions.SummaryChanged += summary =>
        {
            foreach (var c in _connections.Values) c.SendEvent("session.summary", new JsonObject { ["session"] = summary.DeepClone() });
            Changed?.Invoke();
        };
        Sessions.SessionRemoved += id =>
        {
            foreach (var c in _connections.Values) c.SendEvent("session.removed", new JsonObject { ["sessionId"] = id });
            Changed?.Invoke();
        };
    }

    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Conduit");

    public async Task StartAsync(CancellationToken ct = default)
    {
        _direct = new DirectServer(this);
        await _direct.StartAsync(Config.Port, ct);
        RestartRelay();
        _ = Agents.RefreshAsync().ContinueWith(_ => Changed?.Invoke(), TaskScheduler.Default);
        Changed?.Invoke();
    }

    public bool DirectListening => _direct?.Running == true;
    public string? DirectError => _direct?.Error;
    public string RelayStatus => _relay is null ? "disabled" : _relay.Status;
    public string? RelayError => _relay?.LastError;

    public void RestartRelay()
    {
        var old = _relay;
        _relay = null;
        if (old is not null) _ = old.DisposeAsync().AsTask();
        if (!string.IsNullOrWhiteSpace(Config.RelayUrl))
        {
            _relay = new RelayClient(this, Config.RelayUrl!);
            _relay.StatusChanged += () => Changed?.Invoke();
            _relay.Start();
        }
        Changed?.Invoke();
    }

    public async Task RestartDirectAsync()
    {
        if (_direct is not null) await _direct.DisposeAsync();
        _direct = new DirectServer(this);
        await _direct.StartAsync(Config.Port, CancellationToken.None);
        Changed?.Invoke();
    }

    // ---------------- config / devices ----------------

    public void SaveConfig()
    {
        lock (_configLock) Config.Save(ConfigPath);
    }

    public void Update(Action<HostConfig> change)
    {
        lock (_configLock)
        {
            change(Config);
            Config.Save(ConfigPath);
        }
        Changed?.Invoke();
    }

    public PairedDevice? FindDevice(string id)
    {
        lock (_configLock) return Config.Devices.FirstOrDefault(d => d.Id == id);
    }

    public PairedDevice AddDevice(string id, string name, byte[] publicKey)
    {
        var device = new PairedDevice { Id = id, Name = name, PublicKey = B64.Std(publicKey), PairedAt = DateTimeOffset.UtcNow };
        Update(c =>
        {
            c.Devices.RemoveAll(d => d.Id == id);
            c.Devices.Add(device);
        });
        Log($"Paired new device: {name}");
        RegeneratePairingToken(); // one QR code = one device
        return device;
    }

    public void RemoveDevice(string id)
    {
        Update(c => c.Devices.RemoveAll(d => d.Id == id));
        foreach (var conn in _connections.Values.Where(c => c.Device?.Id == id)) conn.Close();
    }

    public bool TryConsumePairingToken(string token)
    {
        lock (_configLock)
        {
            if (DateTimeOffset.UtcNow - Config.PairingTokenCreated > PairingTokenLifetime) return false;
            var a = Encoding.UTF8.GetBytes(token);
            var b = Encoding.UTF8.GetBytes(Config.PairingToken);
            return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
        }
    }

    public void RegeneratePairingToken() => Update(c =>
    {
        c.PairingToken = Ids.NewToken();
        c.PairingTokenCreated = DateTimeOffset.UtcNow;
    });

    public void RememberDirectory(string dir) => Update(c =>
    {
        c.RecentDirectories.RemoveAll(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
        c.RecentDirectories.Insert(0, dir);
        if (c.RecentDirectories.Count > 20) c.RecentDirectories.RemoveRange(20, c.RecentDirectories.Count - 20);
    });

    public void RegisterPush(string deviceId, string? token, string? env, string? bundleId) => Update(c =>
    {
        var d = c.Devices.FirstOrDefault(x => x.Id == deviceId);
        if (d is null) return;
        d.LiveActivityToken = token;
        d.ApnsEnvironment = env;
        d.BundleId = bundleId;
    });

    public List<EndpointInfo> Endpoints() => NetworkEndpoints.Discover(Config.Port, Config.ExtraEndpoints);

    /// <summary>conduit://pair?d=… — the QR code content. Contains no secret besides the one-time pairing token.</summary>
    public string PairingLink()
    {
        var payload = new JsonObject
        {
            ["v"] = 1,
            ["id"] = Identity.HostId,
            ["n"] = Config.HostName,
            ["k"] = B64.Std(Identity.KeyAgreementPublic),
            ["t"] = Config.PairingToken,
            ["e"] = new JsonArray(Endpoints().Select(e => (JsonNode)JsonValue.Create(e.Url)!).ToArray()),
        };
        if (!string.IsNullOrWhiteSpace(Config.RelayUrl)) payload["r"] = Config.RelayUrl;
        return "conduit://pair?d=" + B64.Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
    }

    public JsonObject Info()
    {
        var agents = new JsonArray(Agents.List().Select(a => (JsonNode)new JsonObject
        {
            ["id"] = a.Id,
            ["name"] = a.Name,
            ["vendor"] = a.Vendor,
            ["available"] = a.Available,
            ["version"] = a.Version,
            ["detail"] = a.Detail,
        }).ToArray());
        return new JsonObject
        {
            ["hostId"] = Identity.HostId,
            ["hostName"] = Config.HostName,
            ["version"] = HostInfo.Version,
            ["os"] = RuntimeInformation.OSDescription,
            ["agents"] = agents,
            ["endpoints"] = new JsonArray(Endpoints().Select(e => (JsonNode)JsonValue.Create(e.Url)!).ToArray()),
            ["relay"] = Config.RelayUrl,
            ["apns"] = Config.Apns.Enabled,
        };
    }

    // ---------------- connections ----------------

    public IReadOnlyCollection<ClientConnection> Connections => _connections.Values.ToList();

    public HashSet<string> ConnectedDeviceIds() => _connections.Values.Where(c => c.Device is not null).Select(c => c.Device!.Id).ToHashSet();

    internal void OnConnected(ClientConnection c)
    {
        _connections[c.Id] = c;
        Update(cfg =>
        {
            var d = cfg.Devices.FirstOrDefault(x => x.Id == c.Device!.Id);
            if (d is not null) d.LastSeen = DateTimeOffset.UtcNow;
        });
        Log($"{c.Device!.Name} connected via {c.Route} ({c.RemoteAddress})");
    }

    internal void OnDisconnected(ClientConnection c)
    {
        _connections.TryRemove(c.Id, out _);
        Log($"{c.Device?.Name} disconnected");
        Changed?.Invoke();
    }

    // ---------------- logging ----------------

    public IEnumerable<string> RecentLog => _log.ToArray();

    public void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        _log.Enqueue(line);
        while (_log.Count > 300) _log.TryDequeue(out _);
        LogAdded?.Invoke(line);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _connections.Values) c.Close();
        if (_relay is not null) await _relay.DisposeAsync();
        if (_direct is not null) await _direct.DisposeAsync();
        Pusher.Dispose();
        await Sessions.DisposeAsync();
        Identity.Dispose();
    }
}
