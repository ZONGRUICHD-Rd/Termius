using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// Conduit relay: a dumb, stateless rendezvous that pipes WebSocket frames between a phone and a desktop host.
// Every frame is end-to-end encrypted between phone and host; the relay cannot read or forge traffic.
// Hosts prove ownership of their id with an ECDSA P-256 signature (id = base64url(sha256(pubkey))[..22]).
//
//   host control : GET /v1/host?id&pub&ts&sig            (one long-lived socket per host)
//   phone        : GET /v1/connect/{hostId}              (relay asks the host to dial back)
//   host data    : GET /v1/accept/{connId}?id&pub&ts&sig (bridged 1:1 with the waiting phone)

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Conduit", LogLevel.Information);
var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 8080;
builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(port, l => l.Protocols = HttpProtocols.Http1);
    o.AddServerHeader = false;
});

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Conduit.Relay");
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var hosts = new ConcurrentDictionary<string, HostControl>();
var pending = new ConcurrentDictionary<string, PendingConnection>();
var perIp = new ConcurrentDictionary<string, int>();
const int MaxPendingPerHost = 32;
const int MaxConnectionsPerIp = 64;

app.MapGet("/", () => Results.Text("conduit relay\n"));
app.MapGet("/healthz", () => Results.Json(new { ok = true, hosts = hosts.Count, pending = pending.Count }));

app.Map("/v1/host", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var hostId = Auth.Verify(ctx.Request.Query);
    if (hostId is null) { ctx.Response.StatusCode = 401; return; }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var control = new HostControl(ws);
    if (hosts.TryGetValue(hostId, out var previous)) previous.Abort();
    hosts[hostId] = control;
    log.LogInformation("host {Host} online", hostId);
    try
    {
        var buf = new byte[1024];
        while (ws.State == WebSocketState.Open)
        {
            var r = await ws.ReceiveAsync(buf, ctx.RequestAborted);
            if (r.MessageType == WebSocketMessageType.Close) break;
        }
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
    finally
    {
        hosts.TryRemove(new KeyValuePair<string, HostControl>(hostId, control));
        log.LogInformation("host {Host} offline", hostId);
    }
});

app.Map("/v1/connect/{hostId}", async (HttpContext ctx, string hostId) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    if (perIp.AddOrUpdate(ip, 1, (_, n) => n + 1) > MaxConnectionsPerIp)
    {
        perIp.AddOrUpdate(ip, 0, (_, n) => n - 1);
        ctx.Response.StatusCode = 429;
        return;
    }
    try
    {
        using var phone = await ctx.WebSockets.AcceptWebSocketAsync();
        if (!hosts.TryGetValue(hostId, out var host))
        {
            await phone.CloseAsync((WebSocketCloseStatus)4404, "host_offline", CancellationToken.None);
            return;
        }
        if (pending.Values.Count(x => x.HostId == hostId) >= MaxPendingPerHost)
        {
            await phone.CloseAsync((WebSocketCloseStatus)4429, "busy", CancellationToken.None);
            return;
        }

        var connId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var conn = new PendingConnection(hostId);
        pending[connId] = conn;
        try
        {
            await host.SendAsync(JsonSerializer.Serialize(new { type = "incoming", conn = connId }));
            WebSocket hostData;
            try { hostData = await conn.HostSocket.Task.WaitAsync(TimeSpan.FromSeconds(10), ctx.RequestAborted); }
            catch (TimeoutException)
            {
                await phone.CloseAsync((WebSocketCloseStatus)4408, "host_timeout", CancellationToken.None);
                return;
            }
            finally { pending.TryRemove(connId, out _); }

            await Bridge.RunAsync(phone, hostData, ctx.RequestAborted);
            conn.Done.TrySetResult();
        }
        finally
        {
            pending.TryRemove(connId, out _);
            conn.Done.TrySetResult();
        }
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
    finally
    {
        perIp.AddOrUpdate(ip, 0, (_, n) => Math.Max(0, n - 1));
    }
});

app.Map("/v1/accept/{connId}", async (HttpContext ctx, string connId) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var hostId = Auth.Verify(ctx.Request.Query);
    if (hostId is null) { ctx.Response.StatusCode = 401; return; }
    if (!pending.TryGetValue(connId, out var conn) || conn.HostId != hostId) { ctx.Response.StatusCode = 404; return; }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    if (!conn.HostSocket.TrySetResult(ws)) return;
    // Keep this request (and so the socket) alive until the bridge finishes.
    try { await conn.Done.Task.WaitAsync(ctx.RequestAborted); }
    catch (OperationCanceledException) { }
});

log.LogInformation("Conduit relay listening on :{Port}", port);
app.Run();

sealed class HostControl(WebSocket ws)
{
    private readonly SemaphoreSlim _send = new(1, 1);

    public async Task SendAsync(string text)
    {
        await _send.WaitAsync();
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _send.Release(); }
    }

    public void Abort() => ws.Abort();
}

sealed class PendingConnection(string hostId)
{
    public string HostId { get; } = hostId;
    public TaskCompletionSource<WebSocket> HostSocket { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

static class Bridge
{
    public static async Task RunAsync(WebSocket a, WebSocket b, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var t1 = Pump(a, b, cts.Token);
        var t2 = Pump(b, a, cts.Token);
        await Task.WhenAny(t1, t2);
        cts.Cancel();
        await Task.WhenAll(Quiet(t1), Quiet(t2));
        foreach (var ws in new[] { a, b })
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "peer_closed", CancellationToken.None);
            }
            catch (Exception) { }
        }
    }

    private static async Task Quiet(Task t)
    {
        try { await t; } catch (Exception) { }
    }

    private static async Task Pump(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var r = await from.ReceiveAsync(buffer.AsMemory(), ct);
            if (r.MessageType == WebSocketMessageType.Close) return;
            await to.SendAsync(buffer.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, ct);
        }
    }
}

static class Auth
{
    /// <summary>Returns the host id if the query proves ownership of it, otherwise null.</summary>
    public static string? Verify(IQueryCollection q)
    {
        try
        {
            string id = q["id"]!, pub = q["pub"]!, ts = q["ts"]!, sig = q["sig"]!;
            if (string.IsNullOrEmpty(id) || !long.TryParse(ts, out var t)) return null;
            if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t) > 300) return null;
            var pubBytes = FromB64Url(pub);
            if (pubBytes.Length != 65 || pubBytes[0] != 4) return null;
            if (ToB64Url(SHA256.HashData(pubBytes))[..22] != id) return null;
            using var key = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = pubBytes[1..33], Y = pubBytes[33..65] },
            });
            var ok = key.VerifyData(Encoding.UTF8.GetBytes($"conduit-relay-v1|{id}|{t}"), FromB64Url(sig),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return ok ? id : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ToB64Url(byte[] d) => Convert.ToBase64String(d).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t + new string('=', (4 - t.Length % 4) % 4));
    }
}
