using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conduit.Host.Core;

public sealed class PairedDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Device static P-256 public key, base64 X9.63.</summary>
    public string PublicKey { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    /// <summary>ActivityKit push token (hex) for Live Activity updates over APNs, if registered.</summary>
    public string? LiveActivityToken { get; set; }
    public string? ApnsEnvironment { get; set; }
    public string? BundleId { get; set; }
}

public sealed class ApnsSettings
{
    public bool Enabled { get; set; }
    public string TeamId { get; set; } = "";
    public string KeyId { get; set; } = "";
    /// <summary>Contents of the AuthKey_XXXX.p8 file (PEM).</summary>
    public string PrivateKeyPem { get; set; } = "";
}

/// <summary>Cloudflare Tunnel: "quick" = free random *.trycloudflare.com URL; "token" = your own named tunnel + domain.</summary>
public sealed class CloudflareSettings
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "quick";
    /// <summary>Tunnel token from the Cloudflare dashboard (token mode).</summary>
    public string Token { get; set; } = "";
    /// <summary>Public hostname routed to http://localhost:47821 in the dashboard, e.g. conduit.example.com (token mode).</summary>
    public string Hostname { get; set; } = "";
    /// <summary>Optional explicit path to cloudflared(.exe); otherwise PATH, then an automatic download.</summary>
    public string? CloudflaredPath { get; set; }
}

/// <summary>DDNS / port forwarding: a domain that resolves to this PC and a forwarded port.</summary>
public sealed class DdnsSettings
{
    public bool Enabled { get; set; }
    public string Domain { get; set; } = "";
    public int Port { get; set; } = 47821;
    /// <summary>Use wss:// (a TLS reverse proxy in front of the host).</summary>
    public bool Tls { get; set; }

    [JsonIgnore]
    public string? Url => Enabled && !string.IsNullOrWhiteSpace(Domain)
        ? $"{(Tls ? "wss" : "ws")}://{Domain.Trim().TrimEnd('/')}:{Port}"
        : null;
}

public sealed class HostConfig
{
    public string HostName { get; set; } = Environment.MachineName;
    public int Port { get; set; } = 47821;
    /// <summary>Optional relay server, e.g. wss://relay.example.com. Enables connecting from anywhere without port forwarding.</summary>
    public string? RelayUrl { get; set; }
    /// <summary>Extra addresses phones should try (e.g. a DDNS name or Tailscale MagicDNS "ws://my-pc.tailnet.ts.net:47821").</summary>
    public List<string> ExtraEndpoints { get; set; } = [];
    public string PairingToken { get; set; } = "";
    public DateTimeOffset PairingTokenCreated { get; set; }
    public List<PairedDevice> Devices { get; set; } = [];
    /// <summary>Agent id → explicit executable path, overrides PATH discovery.</summary>
    public Dictionary<string, string> AgentPaths { get; set; } = [];
    public List<string> RecentDirectories { get; set; } = [];
    public ApnsSettings Apns { get; set; } = new();
    /// <summary>Allow direct connections on the local network (and Tailscale).</summary>
    public bool LanEnabled { get; set; } = true;
    public CloudflareSettings Cloudflare { get; set; } = new();
    public DdnsSettings Ddns { get; set; } = new();
    public bool EnableEchoAgent { get; set; }

    public static HostConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJson.Default.HostConfig) ?? new HostConfig();
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }
        return new HostConfig();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, ConfigJson.Default.HostConfig));
        File.Move(tmp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HostConfig))]
internal partial class ConfigJson : JsonSerializerContext;
