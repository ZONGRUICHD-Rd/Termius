using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Conduit.Host.Core.Protocol;
using Conduit.Host.Core.Security;

namespace Conduit.Host.Core.Push;

/// <summary>
/// Optional: keeps the iPhone's Dynamic Island / Lock Screen Live Activity updating while the app is suspended,
/// by sending ActivityKit pushes straight from this computer to APNs (needs an Apple Developer APNs key).
/// Without it, the Live Activity is updated by the app itself while it runs and shows a ticking timer otherwise.
/// The content-state shape must match <c>AgentActivityAttributes.ContentState</c> in the iOS app.
/// </summary>
public sealed class ApnsLiveActivityPusher : IDisposable
{
    private readonly HostRuntime _host;
    private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromMinutes(30) });
    private readonly Timer _timer;
    private string? _lastPayloadKey;
    private DateTimeOffset _lastPush = DateTimeOffset.MinValue;
    private string? _jwt;
    private DateTimeOffset _jwtIssued;
    private bool _pendingEnd;
    private int _busy;

    public ApnsLiveActivityPusher(HostRuntime host)
    {
        _host = host;
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>Same aggregation the app performs locally (see LiveActivityController.swift).</summary>
    public static JsonObject? BuildContentState(IEnumerable<JsonObject> summaries, out bool anyActive)
    {
        var list = summaries.ToList();
        var active = list.Where(s => SessionStates.IsActive((string?)s["state"] ?? "")).ToList();
        anyActive = active.Count > 0;
        var primary = active
            .OrderByDescending(s => (string?)s["state"] == SessionStates.Waiting)
            .ThenByDescending(s => (long?)s["updatedAt"] ?? 0)
            .FirstOrDefault()
            ?? list.OrderByDescending(s => (long?)s["updatedAt"] ?? 0).FirstOrDefault();
        if (primary is null) return null;
        return new JsonObject
        {
            ["sessionId"] = (string?)primary["id"] ?? "",
            ["title"] = (string?)primary["title"] ?? "",
            ["agent"] = (string?)primary["agent"] ?? "",
            ["state"] = (string?)primary["state"] ?? "",
            ["detail"] = (string?)primary["detail"] ?? "",
            ["done"] = (int?)primary["done"] ?? 0,
            ["total"] = (int?)primary["total"] ?? 0,
            ["startedAt"] = ((long?)primary["turnStartedAt"] ?? 0) / 1000.0,
            ["running"] = active.Count,
            ["waiting"] = active.Count(s => (string?)s["state"] == SessionStates.Waiting),
            ["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        };
    }

    private async Task TickAsync()
    {
        var apns = _host.Config.Apns;
        if (!apns.Enabled || string.IsNullOrWhiteSpace(apns.PrivateKeyPem)) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var targets = _host.Config.Devices.Where(d => !string.IsNullOrEmpty(d.LiveActivityToken) && !string.IsNullOrEmpty(d.BundleId)).ToList();
            if (targets.Count == 0) return;
            // Phones that are connected update their own activity; push only to the ones that aren't.
            var connected = _host.ConnectedDeviceIds();
            targets = targets.Where(t => !connected.Contains(t.Id)).ToList();
            if (targets.Count == 0) return;

            var state = BuildContentState(_host.Sessions.All.Select(s => s.Summary()), out var anyActive);
            if (state is null) return;
            var isEnd = !anyActive;
            if (isEnd && !_pendingEnd) return;
            var keyObj = (JsonObject)state.DeepClone();
            keyObj.Remove("updatedAt");
            var key = keyObj.ToJsonString() + isEnd;
            var urgent = (string?)state["state"] is SessionStates.Waiting or SessionStates.Idle or SessionStates.Error;
            // Budget-friendly: only on change, and at most every 10s unless the state is urgent.
            if (key == _lastPayloadKey) return;
            if (!urgent && DateTimeOffset.UtcNow - _lastPush < TimeSpan.FromSeconds(10)) return;
            _lastPayloadKey = key;
            _lastPush = DateTimeOffset.UtcNow;
            _pendingEnd = anyActive;

            foreach (var d in targets) await SendAsync(d, state, isEnd, urgent);
        }
        catch (Exception ex)
        {
            _host.Log("APNs: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task SendAsync(PairedDevice device, JsonObject contentState, bool end, bool urgent)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var aps = new JsonObject
        {
            ["timestamp"] = now,
            ["event"] = end ? "end" : "update",
            ["content-state"] = contentState.DeepClone(),
            ["stale-date"] = now + 15 * 60,
        };
        if (end) aps["dismissal-date"] = now + 10 * 60;
        if ((string?)contentState["state"] == SessionStates.Waiting)
            aps["alert"] = new JsonObject { ["title"] = "Approval needed", ["body"] = (string?)contentState["detail"] ?? "" };
        var payload = new JsonObject { ["aps"] = aps };

        var host = device.ApnsEnvironment == "production" ? "api.push.apple.com" : "api.sandbox.push.apple.com";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/3/device/{device.LiveActivityToken}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("authorization", "bearer " + Jwt());
        req.Headers.TryAddWithoutValidation("apns-push-type", "liveactivity");
        req.Headers.TryAddWithoutValidation("apns-topic", device.BundleId + ".push-type.liveactivity");
        req.Headers.TryAddWithoutValidation("apns-priority", urgent ? "10" : "5");
        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            _host.Log($"APNs {(int)resp.StatusCode} for {device.Name}: {body}");
            if (resp.StatusCode == HttpStatusCode.Gone || body.Contains("BadDeviceToken"))
                _host.RegisterPush(device.Id, null, null, null);
        }
    }

    private string Jwt()
    {
        var apns = _host.Config.Apns;
        if (_jwt is not null && DateTimeOffset.UtcNow - _jwtIssued < TimeSpan.FromMinutes(40)) return _jwt;
        using var key = ECDsa.Create();
        key.ImportFromPem(apns.PrivateKeyPem);
        var iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64.Url(Encoding.UTF8.GetBytes(new JsonObject { ["alg"] = "ES256", ["kid"] = apns.KeyId }.ToJsonString()));
        var claims = B64.Url(Encoding.UTF8.GetBytes(new JsonObject { ["iss"] = apns.TeamId, ["iat"] = iat }.ToJsonString()));
        var signingInput = $"{header}.{claims}";
        var sig = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        _jwt = $"{signingInput}.{B64.Url(sig)}";
        _jwtIssued = DateTimeOffset.UtcNow;
        return _jwt;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
