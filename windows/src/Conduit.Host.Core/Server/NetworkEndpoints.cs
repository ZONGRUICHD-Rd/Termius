using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Conduit.Host.Core.Server;

public sealed record EndpointInfo(string Url, string Kind, string Interface);

/// <summary>
/// Addresses the phone can try directly. The phone races all of them (plus the relay) and keeps the
/// fastest; Tailscale / ZeroTier addresses give peer-to-peer connectivity from anywhere without a relay.
/// </summary>
public static class NetworkEndpoints
{
    public static List<EndpointInfo> Discover(int port, IEnumerable<string> extra)
    {
        var list = new List<EndpointInfo>();
        foreach (var e in extra.Where(e => !string.IsNullOrWhiteSpace(e)))
            list.Add(new EndpointInfo(e.Trim(), "custom", "configured"));

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch (NetworkInformationException) { continue; }
                foreach (var ua in props.UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                    var kind = Classify(ip, ni.Name + " " + ni.Description);
                    if (kind is null) continue;
                    list.Add(new EndpointInfo($"ws://{ip}:{port}", kind, ni.Name));
                }
            }
        }
        catch (NetworkInformationException) { }

        // Overlay networks first: they work from anywhere. Then LAN.
        return list
            .DistinctBy(e => e.Url)
            .OrderBy(e => e.Kind switch { "custom" => 0, "tailscale" => 1, "overlay" => 2, "lan" => 3, _ => 4 })
            .ToList();
    }

    private static string? Classify(IPAddress ip, string ifaceName)
    {
        var b = ip.GetAddressBytes();
        if (b[0] == 169 && b[1] == 254) return null; // link-local
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            return "tailscale"; // CGNAT range used by Tailscale
        if (ifaceName.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) || ifaceName.Contains("WireGuard", StringComparison.OrdinalIgnoreCase))
            return "overlay";
        if (ifaceName.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) || ifaceName.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || ifaceName.Contains("VMware", StringComparison.OrdinalIgnoreCase) || ifaceName.Contains("docker", StringComparison.OrdinalIgnoreCase))
            return null; // Hyper-V / WSL / VM switches are unreachable from the phone
        if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168))
            return "lan";
        return "public";
    }
}
