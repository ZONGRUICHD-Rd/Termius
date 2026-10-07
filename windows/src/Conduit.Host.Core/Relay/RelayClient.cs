using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Conduit.Host.Core.Security;
using Conduit.Host.Core.Server;

namespace Conduit.Host.Core.Relay;

/// <summary>
/// Keeps an outbound control socket to the relay (works behind NAT / CGNAT / corporate firewalls).
/// For each incoming phone the relay sends {"type":"incoming","conn":id}; we open a dedicated data socket
/// for it and run the normal E2E handshake through the relay, which only ever sees ciphertext.
/// </summary>
public sealed class RelayClient : IAsyncDisposable
{
    private readonly HostRuntime _host;
    private readonly string _relay;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public string Status { get; private set; } = "offline";
    public string? LastError { get; private set; }
    public event Action? StatusChanged;

    public RelayClient(HostRuntime host, string relayUrl)
    {
        _host = host;
        _relay = relayUrl.TrimEnd('/');
    }

    public void Start() => _loop = Task.Run(Loop);

    private void SetStatus(string status, string? error = null)
    {
        Status = status;
        LastError = error;
        StatusChanged?.Invoke();
    }

    private Uri AuthUri(string path)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var id = _host.Identity;
        var q = $"id={id.HostId}&pub={B64.Url(id.SigningPublic)}&ts={ts}&sig={id.SignRelayChallenge(ts)}";
        return new Uri($"{_relay}{path}?{q}");
    }

    private static ClientWebSocket NewSocket()
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.SetRequestHeader("User-Agent", $"ConduitHost/{HostInfo.Version}");
        return ws;
    }

    private async Task Loop()
    {
        var backoff = 1000;
        var buffer = new byte[4096];
        while (!_cts.IsCancellationRequested)
        {
            using var ws = NewSocket();
            try
            {
                SetStatus("connecting");
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(AuthUri("/v1/host"), connectTimeout.Token);
                }
                SetStatus("online");
                _host.Log($"Relay online: {_relay}");
                backoff = 1000;

                while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buffer, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (r.MessageType != WebSocketMessageType.Text || !r.EndOfMessage) continue;
                    var msg = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count)) as JsonObject;
                    if ((string?)msg?["type"] == "incoming" && (string?)msg["conn"] is { } conn)
                        _ = Task.Run(() => AcceptAsync(conn));
                }
                SetStatus("offline", ws.CloseStatusDescription);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                SetStatus("offline", ex.Message);
            }
            try { await Task.Delay(backoff + Random.Shared.Next(0, 500), _cts.Token); }
            catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, 30_000);
        }
        SetStatus("offline");
    }

    private async Task AcceptAsync(string connId)
    {
        using var ws = NewSocket();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(AuthUri($"/v1/accept/{Uri.EscapeDataString(connId)}"), timeout.Token);
            await new ClientConnection(_host, ws, "relay", "relay", _cts.Token).RunAsync();
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            _host.Log($"Relay accept {connId} failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(2000));
    }
}
