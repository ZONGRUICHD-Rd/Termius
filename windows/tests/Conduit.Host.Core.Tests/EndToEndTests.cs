using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Conduit.Host.Core.Protocol;
using Conduit.Host.Core.Security;
using Xunit;

namespace Conduit.Host.Core.Tests;

/// <summary>Spins up a real host (Kestrel + demo agent) and drives it like the phone does.</summary>
public class EndToEndTests : IAsyncLifetime
{
    private string _dir = "";
    private HostRuntime _host = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "conduit-test-" + Guid.NewGuid().ToString("n"));
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _host = new HostRuntime(_dir);
        _host.Update(c =>
        {
            c.Port = _port;
            c.EnableEchoAgent = true;
        });
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private (string Url, byte[] HostKey, string HostId, string Token) ParsePairing()
    {
        var link = _host.PairingLink();
        var json = (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(B64.FromUrl(link.Split("d=")[1])))!;
        return ($"ws://127.0.0.1:{_port}/v1/connect", B64.FromStd((string)json["k"]!), (string)json["id"]!, (string)json["t"]!);
    }

    [Fact]
    public async Task PairCreateSessionStreamAndApprove()
    {
        var (url, hostKey, hostId, token) = ParsePairing();
        using var phoneKey = P256.NewKeyAgreementKey();

        // Unpaired without token → rejected.
        await Assert.ThrowsAsync<InvalidOperationException>(() => TestClient.ConnectAsync(url, phoneKey, hostKey, hostId, null));

        await using (var first = await TestClient.ConnectAsync(url, phoneKey, hostKey, hostId, token))
        {
            Assert.True((bool?)first.Welcome["paired"]);
            var info = await first.CallAsync("host.info");
            Assert.Contains(((JsonArray)info["agents"]!), a => (string?)a!["id"] == "demo");
        }

        // Token is single-use, but the paired key now connects without it.
        await using var c = await TestClient.ConnectAsync(url, phoneKey, hostKey, hostId, null);
        Assert.False((bool?)c.Welcome["paired"]);

        var created = await c.CallAsync("session.create", new JsonObject { ["agent"] = "demo", ["cwd"] = _dir });
        var sessionId = (string)created["session"]!["id"]!;
        var sub = await c.CallAsync("session.subscribe", new JsonObject { ["sessionId"] = sessionId, ["since"] = 0 });
        Assert.NotNull(sub["events"]);

        await c.CallAsync("session.send", new JsonObject { ["sessionId"] = sessionId, ["text"] = "hello" });

        var approval = await c.WaitEventAsync(e => (string?)e["e"] == "session.event" && (string?)e["p"]!["ev"]!["k"] == Ev.KApproval);
        var aid = (string)approval["p"]!["ev"]!["aid"]!;

        var summary = await c.WaitEventAsync(e => (string?)e["e"] == "session.summary" && (string?)e["p"]!["session"]!["state"] == SessionStates.Waiting);
        Assert.Equal(1, (int?)summary["p"]!["session"]!["approvals"]);

        await c.CallAsync("approval.respond", new JsonObject { ["sessionId"] = sessionId, ["approvalId"] = aid, ["decision"] = "allow" });

        var final = await c.WaitEventAsync(e => (string?)e["e"] == "session.event" && (string?)e["p"]!["ev"]!["k"] == Ev.KMessage);
        Assert.Contains("hello", (string)final["p"]!["ev"]!["text"]!);
        await c.WaitEventAsync(e => (string?)e["e"] == "session.summary" && (string?)e["p"]!["session"]!["state"] == SessionStates.Idle);

        // Reconnect catch-up: compacted history, no raw deltas.
        var replay = await c.CallAsync("session.subscribe", new JsonObject { ["sessionId"] = sessionId, ["since"] = 0 });
        var kinds = ((JsonArray)replay["events"]!).Select(e => (string?)e!["ev"]!["k"]).ToList();
        Assert.DoesNotContain(Ev.KDelta, kinds);
        Assert.Contains(Ev.KUser, kinds);
        Assert.Contains(Ev.KMessage, kinds);
        Assert.Equal(1, kinds.Count(k => k == Ev.KPlan));

        var fs = await c.CallAsync("fs.list", new JsonObject { ["path"] = _dir });
        Assert.Equal(Path.GetFullPath(_dir), (string?)fs["path"]);
    }

    [Fact]
    public async Task SessionsSurviveHostRestart()
    {
        var (url, hostKey, hostId, token) = ParsePairing();
        using var phoneKey = P256.NewKeyAgreementKey();
        string sessionId;
        await using (var c = await TestClient.ConnectAsync(url, phoneKey, hostKey, hostId, token))
        {
            var created = await c.CallAsync("session.create", new JsonObject { ["agent"] = "demo", ["cwd"] = _dir, ["title"] = "Persist me" });
            sessionId = (string)created["session"]!["id"]!;
            await c.CallAsync("session.subscribe", new JsonObject { ["sessionId"] = sessionId, ["since"] = 0 });
            await c.CallAsync("session.send", new JsonObject { ["sessionId"] = sessionId, ["text"] = "remember" });
            await c.WaitEventAsync(e => (string?)e["e"] == "session.event" && (string?)e["p"]!["ev"]!["k"] == Ev.KApproval);
        }

        await _host.DisposeAsync();
        _host = new HostRuntime(_dir);
        await _host.StartAsync();

        await using var c2 = await TestClient.ConnectAsync(url, phoneKey, hostKey, hostId, null);
        var list = await c2.CallAsync("sessions.list");
        var s = ((JsonArray)list["sessions"]!).Single(x => (string?)x!["id"] == sessionId)!;
        Assert.Equal("Persist me", (string?)s["title"]);
        Assert.Equal(SessionStates.Stopped, (string?)s["state"]);
        var replay = await c2.CallAsync("session.subscribe", new JsonObject { ["sessionId"] = sessionId, ["since"] = 0 });
        Assert.Contains(((JsonArray)replay["events"]!), e => (string?)e!["ev"]!["text"] == "remember");
    }
}

/// <summary>
/// Full path through a running relay. Start one first:
///   PORT=18080 dotnet run --project relay/src &amp;  CONDUIT_TEST_RELAY=ws://127.0.0.1:18080 dotnet test
/// Skipped (passes trivially) when CONDUIT_TEST_RELAY is not set.
/// </summary>
public class RelayEndToEndTests
{
    [Fact]
    public async Task ConnectsThroughRelay()
    {
        var relay = Environment.GetEnvironmentVariable("CONDUIT_TEST_RELAY");
        if (string.IsNullOrEmpty(relay)) return;

        var dir = Path.Combine(Path.GetTempPath(), "conduit-relay-" + Guid.NewGuid().ToString("n"));
        await using var host = new HostRuntime(dir);
        host.Update(c =>
        {
            c.Port = 0;
            c.RelayUrl = relay;
            c.EnableEchoAgent = true;
        });
        await host.StartAsync();
        for (var i = 0; i < 50 && host.RelayStatus != "online"; i++) await Task.Delay(100);
        Assert.Equal("online", host.RelayStatus);

        var json = (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(B64.FromUrl(host.PairingLink().Split("d=")[1])))!;
        using var phoneKey = P256.NewKeyAgreementKey();
        await using var c = await TestClient.ConnectAsync($"{relay}/v1/connect/{host.Identity.HostId}", phoneKey,
            B64.FromStd((string)json["k"]!), host.Identity.HostId, (string)json["t"]!);
        Assert.Equal("relay", (string?)c.Welcome["route"]);
        var created = await c.CallAsync("session.create", new JsonObject { ["agent"] = "demo", ["cwd"] = dir, ["prompt"] = "via relay" });
        Assert.NotNull(created["session"]);
        var info = await c.CallAsync("host.info");
        Assert.Equal(host.Identity.HostId, (string?)info["hostId"]);
    }
}
