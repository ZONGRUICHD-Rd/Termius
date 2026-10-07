using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Host.Core.Server;

/// <summary>
/// Kestrel listener for direct connections (LAN / Tailscale / port-forward). Plain ws:// is fine because every
/// frame is end-to-end encrypted and authenticated by the handshake; TLS would only add a round trip.
/// </summary>
public sealed class DirectServer : IAsyncDisposable
{
    private readonly HostRuntime _host;
    private WebApplication? _app;

    public DirectServer(HostRuntime host) => _host = host;

    public bool Running => _app is not null;
    public string? Error { get; private set; }

    public async Task StartAsync(int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.ListenAnyIP(port, l => l.Protocols = HttpProtocols.Http1);
            o.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
            o.AddServerHeader = false;
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

        app.MapGet("/v1/ping", () => Results.Text(
            new JsonObject { ["app"] = "conduit", ["v"] = 1, ["hostId"] = _host.Identity.HostId }.ToJsonString(), "application/json"));

        app.Map("/v1/connect", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            var route = remote.StartsWith("100.") ? "tailscale" : "direct";
            await new ClientConnection(_host, ws, route, remote, ctx.RequestAborted).RunAsync();
        });

        try
        {
            await app.StartAsync(ct);
            _app = app;
            Error = null;
            _host.Log($"Listening on port {port}");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _host.Log($"Could not listen on port {port}: {ex.Message}");
            await app.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        await _app.DisposeAsync();
        _app = null;
    }
}
