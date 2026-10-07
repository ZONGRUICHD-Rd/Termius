using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Conduit.Host.Core.Security;

namespace Conduit.Host.Core.Server;

/// <summary>
/// One phone connection (direct or via relay). Performs the E2E handshake (docs/PROTOCOL.md §2), then
/// exchanges AES-GCM sealed JSON messages. A single writer task owns the socket; everything else enqueues.
/// </summary>
public sealed class ClientConnection
{
    public const int MaxFrame = 8 * 1024 * 1024;
    private const int MaxQueuedFrames = 50_000;

    private readonly HostRuntime _host;
    private readonly WebSocket _ws;
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, bool> _subscriptions = new();
    private readonly CancellationTokenSource _cts;
    private FrameCipher? _cipher;
    private int _queued;

    public string Id { get; } = Ids.NewId(6);
    public string Route { get; }
    public string RemoteAddress { get; }
    public PairedDevice? Device { get; private set; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    public ClientConnection(HostRuntime host, WebSocket ws, string route, string remoteAddress, CancellationToken ct)
    {
        _host = host;
        _ws = ws;
        Route = route;
        RemoteAddress = remoteAddress;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public bool IsSubscribed(string sessionId) => _subscriptions.ContainsKey(sessionId);
    public void Subscribe(string sessionId) => _subscriptions[sessionId] = true;
    public void Unsubscribe(string sessionId) => _subscriptions.TryRemove(sessionId, out _);

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            if (!await HandshakeAsync(ct)) return;
            _host.OnConnected(this);
            var writer = Task.Run(() => WriteLoop(ct), ct);
            try
            {
                await ReadLoop(ct);
            }
            finally
            {
                _cts.Cancel();
                _outbox.Writer.TryComplete();
                await Task.WhenAny(writer, Task.Delay(1000));
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (CryptographicException ex)
        {
            _host.Log($"Connection {Id}: crypto error, closing ({ex.Message})");
        }
        finally
        {
            if (Device is not null) _host.OnDisconnected(this);
            _cipher?.Dispose();
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch (Exception) { }
        }
    }

    // ---------------- handshake ----------------

    private async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var (type, data) = await ReceiveFrameAsync(16 * 1024, timeout.Token);
        if (type != WebSocketMessageType.Text) return false;

        JsonObject hello;
        try { hello = JsonNode.Parse(data) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return false; }

        if ((string?)hello["type"] != "hello" || (int?)hello["v"] != 1)
            return await Reject("version", "Unsupported protocol version — update the app.", ct);
        if ((string?)hello["hostId"] is { } hid && hid != _host.Identity.HostId)
            return await Reject("wrong_host", "This QR code belongs to a different computer.", ct);

        byte[] clientStatic, clientEph, nonceC;
        try
        {
            clientStatic = B64.FromStd((string?)hello["clientStatic"] ?? "");
            clientEph = B64.FromStd((string?)hello["clientEph"] ?? "");
            nonceC = B64.FromStd((string?)hello["nonce"] ?? "");
            if (nonceC.Length != 16) throw new FormatException();
            P256.ParseX963(clientStatic);
            P256.ParseX963(clientEph);
        }
        catch (Exception) { return await Reject("malformed", "Malformed hello.", ct); }

        var deviceId = Ids.FromPublicKey(clientStatic);
        var device = _host.FindDevice(deviceId);
        var newlyPaired = false;
        if (device is null)
        {
            var token = (string?)hello["pairToken"];
            if (token is null || !_host.TryConsumePairingToken(token))
                return await Reject("unpaired", "This phone isn't paired. Scan a fresh QR code on the computer.", ct);
            device = _host.AddDevice(deviceId, (string?)hello["name"] ?? "iPhone", clientStatic);
            newlyPaired = true;
        }

        using var hostEph = P256.NewKeyAgreementKey();
        var hostEphPub = P256.ExportX963(hostEph);
        var nonceH = RandomNumberGenerator.GetBytes(16);

        var ee = P256.Agree(hostEph, clientEph);
        var es = P256.Agree(_host.Identity.KeyAgreement, clientEph);
        var ss = P256.Agree(_host.Identity.KeyAgreement, clientStatic);
        var (c2h, h2c) = KeySchedule.Derive(ee, es, ss, nonceC, nonceH, clientEph, hostEphPub);
        CryptographicOperations.ZeroMemory(ee);
        CryptographicOperations.ZeroMemory(es);
        CryptographicOperations.ZeroMemory(ss);
        _cipher = new FrameCipher(sendKey: h2c, recvKey: c2h);

        var welcome = new JsonObject
        {
            ["type"] = "welcome",
            ["v"] = 1,
            ["hostId"] = _host.Identity.HostId,
            ["hostName"] = _host.Config.HostName,
            ["hostEph"] = B64.Std(hostEphPub),
            ["nonce"] = B64.Std(nonceH),
            ["paired"] = newlyPaired,
            ["route"] = Route,
        };
        await _ws.SendAsync(Encoding.UTF8.GetBytes(welcome.ToJsonString()), WebSocketMessageType.Text, true, ct);
        Device = device;
        return true;
    }

    private async Task<bool> Reject(string reason, string message, CancellationToken ct)
    {
        var msg = new JsonObject { ["type"] = "reject", ["reason"] = reason, ["message"] = message };
        try
        {
            await _ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);
            await _ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, reason, ct);
        }
        catch (Exception) { }
        _host.Log($"Rejected connection from {RemoteAddress} via {Route}: {reason}");
        return false;
    }

    // ---------------- I/O ----------------

    private async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveFrameAsync(int max, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var r = await _ws.ReceiveAsync(buffer.AsMemory(), ct);
                if (r.MessageType == WebSocketMessageType.Close) throw new OperationCanceledException("closed");
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > max) throw new WebSocketException("Frame too large");
                if (r.EndOfMessage) return (r.MessageType, ms.ToArray());
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (type, frame) = await ReceiveFrameAsync(MaxFrame, ct);
            if (type != WebSocketMessageType.Binary) continue;
            var plain = _cipher!.Open(frame);
            JsonObject? msg;
            try { msg = JsonNode.Parse(plain) as JsonObject; }
            catch (JsonException) { continue; }
            if (msg is null) continue;
            if ((string?)msg["t"] == "req")
                _ = Task.Run(() => _host.Dispatcher.HandleAsync(this, msg, ct), ct);
        }
    }

    private async Task WriteLoop(CancellationToken ct)
    {
        await foreach (var frame in _outbox.Reader.ReadAllAsync(ct))
        {
            Interlocked.Decrement(ref _queued);
            await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
        }
    }

    /// <summary>Encrypts and enqueues a message. Ordering is the order of calls.</summary>
    public void Send(JsonObject message)
    {
        if (_cipher is null || _cts.IsCancellationRequested) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] frame;
        lock (_outbox)
        {
            // Sealing and enqueueing under one lock keeps frame counters in the same order as the queue.
            frame = _cipher.Seal(bytes);
            if (!_outbox.Writer.TryWrite(frame)) return;
        }
        if (Interlocked.Increment(ref _queued) > MaxQueuedFrames)
        {
            _host.Log($"Connection {Id} is too slow, dropping it");
            _cts.Cancel();
        }
    }

    public void SendResult(JsonNode? id, JsonNode? result) =>
        Send(new JsonObject { ["t"] = "res", ["id"] = id?.DeepClone(), ["ok"] = true, ["r"] = result });

    public void SendError(JsonNode? id, string error, string code = "error") =>
        Send(new JsonObject { ["t"] = "res", ["id"] = id?.DeepClone(), ["ok"] = false, ["err"] = error, ["code"] = code });

    public void SendEvent(string name, JsonObject payload) =>
        Send(new JsonObject { ["t"] = "evt", ["e"] = name, ["p"] = payload });

    public void Close() => _cts.Cancel();
}
