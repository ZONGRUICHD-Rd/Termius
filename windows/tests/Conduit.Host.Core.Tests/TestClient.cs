using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Conduit.Host.Core.Security;

namespace Conduit.Host.Core.Tests;

/// <summary>Reference client implementing the phone side of the protocol (mirrors ConduitKit on iOS).</summary>
public sealed class TestClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private FrameCipher _cipher = null!;
    private long _nextId;
    private readonly Dictionary<long, TaskCompletionSource<JsonObject>> _pending = [];
    public Channel<JsonObject> Events { get; } = Channel.CreateUnbounded<JsonObject>();
    public JsonObject Welcome { get; private set; } = null!;
    private Task? _reader;

    public static async Task<TestClient> ConnectAsync(string url, ECDiffieHellman clientStatic, byte[] hostStaticPub, string hostId, string? pairToken)
    {
        var c = new TestClient();
        await c._ws.ConnectAsync(new Uri(url), CancellationToken.None);

        using var eph = P256.NewKeyAgreementKey();
        var ephPub = P256.ExportX963(eph);
        var staticPub = P256.ExportX963(clientStatic);
        var nonceC = RandomNumberGenerator.GetBytes(16);
        var hello = new JsonObject
        {
            ["type"] = "hello",
            ["v"] = 1,
            ["hostId"] = hostId,
            ["name"] = "Test iPhone",
            ["clientStatic"] = B64.Std(staticPub),
            ["clientEph"] = B64.Std(ephPub),
            ["nonce"] = B64.Std(nonceC),
            ["pairToken"] = pairToken,
        };
        await c._ws.SendAsync(Encoding.UTF8.GetBytes(hello.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

        var buf = new byte[65536];
        var r = await c._ws.ReceiveAsync(buf, CancellationToken.None);
        var welcome = (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count))!;
        if ((string?)welcome["type"] != "welcome") throw new InvalidOperationException("rejected: " + welcome.ToJsonString());
        c.Welcome = welcome;

        var hostEph = B64.FromStd((string)welcome["hostEph"]!);
        var nonceH = B64.FromStd((string)welcome["nonce"]!);
        var ee = P256.Agree(eph, hostEph);
        var es = P256.Agree(eph, hostStaticPub);
        var ss = P256.Agree(clientStatic, hostStaticPub);
        var (c2h, h2c) = KeySchedule.Derive(ee, es, ss, nonceC, nonceH, ephPub, hostEph);
        c._cipher = new FrameCipher(sendKey: c2h, recvKey: h2c);
        c._reader = Task.Run(c.ReadLoop);
        return c;
    }

    private async Task ReadLoop()
    {
        var buf = new byte[1 << 20];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                var msg = (JsonObject)JsonNode.Parse(_cipher.Open(ms.ToArray()))!;
                if ((string?)msg["t"] == "res")
                {
                    var id = (long)msg["id"]!;
                    TaskCompletionSource<JsonObject>? tcs;
                    lock (_pending) _pending.Remove(id, out tcs);
                    tcs?.TrySetResult(msg);
                }
                else
                {
                    await Events.Writer.WriteAsync(msg);
                }
            }
        }
        catch (WebSocketException) { }
    }

    public async Task<JsonObject> CallAsync(string method, JsonObject? p = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;
        var msg = new JsonObject { ["t"] = "req", ["id"] = id, ["m"] = method, ["p"] = p ?? new JsonObject() };
        await _ws.SendAsync(_cipher.Seal(JsonSerializer.SerializeToUtf8Bytes(msg)), WebSocketMessageType.Binary, true, CancellationToken.None);
        var res = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if ((bool?)res["ok"] != true) throw new InvalidOperationException((string?)res["err"]);
        return (JsonObject)(res["r"] ?? new JsonObject());
    }

    /// <summary>Waits for a session event matching <paramref name="predicate"/>.</summary>
    public async Task<JsonObject> WaitEventAsync(Func<JsonObject, bool> predicate, int timeoutMs = 10000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            var e = await Events.Reader.ReadAsync(cts.Token);
            if (predicate(e)) return e;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch (Exception) { }
        _ws.Dispose();
        _cipher?.Dispose();
    }
}
