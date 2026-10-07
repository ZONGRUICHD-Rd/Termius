using System.Text.Json.Nodes;
using Conduit.Host.Core.Agents;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Conduit.Host;

public static class Palette
{
    public static SolidColorBrush Brush(byte r, byte g, byte b) => new(ColorHelper.FromArgb(255, r, g, b));

    public static SolidColorBrush State(string state) => state switch
    {
        "running" or "starting" => Brush(0xD9, 0x77, 0x57),
        "waiting" => Brush(0xE6, 0xA8, 0x4A),
        "error" => Brush(0xBF, 0x4D, 0x43),
        "idle" => Brush(0x78, 0x8C, 0x5D),
        _ => Brush(0x8A, 0x87, 0x80),
    };

    public static string StateLabel(string state) => state switch
    {
        "starting" => "Starting",
        "running" => "Working",
        "waiting" => "Needs approval",
        "idle" => "Ready",
        "error" => "Error",
        _ => "Stopped",
    };

    public static string AgentName(string id) => id switch
    {
        "claude" => "Claude",
        "codex" => "Codex",
        "pi" => "Pi",
        "opencode" => "opencode",
        _ => "Demo",
    };

    public static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return t.LocalDateTime.ToString("d MMM");
    }
}

public sealed class SessionItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string AgentName { get; init; } = "";
    public string State { get; init; } = "";
    public string StateLabel { get; init; } = "";
    public SolidColorBrush StateBrush { get; init; } = Palette.State("");
    public string Detail { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Updated { get; init; } = "";
    public double Progress { get; init; }
    public Visibility ProgressVisibility { get; init; } = Visibility.Collapsed;
    public Visibility StopVisibility { get; init; } = Visibility.Collapsed;

    public static SessionItem From(JsonObject s)
    {
        var state = (string?)s["state"] ?? "";
        var done = (int?)s["done"] ?? 0;
        var total = (int?)s["total"] ?? 0;
        var cwd = (string?)s["cwd"] ?? "";
        var active = Core.Protocol.SessionStates.IsActive(state);
        return new SessionItem
        {
            Id = (string?)s["id"] ?? "",
            Title = (string?)s["title"] ?? "",
            AgentName = Palette.AgentName((string?)s["agent"] ?? ""),
            State = state,
            StateLabel = (int?)s["approvals"] is > 0 and var n ? $"{n} to approve" : Palette.StateLabel(state),
            StateBrush = Palette.State(state),
            Detail = active ? (string?)s["detail"] ?? "Working…" : (string?)s["preview"] ?? "",
            Folder = Path.GetFileName(cwd.TrimEnd('\\', '/')) is { Length: > 0 } f ? f : cwd,
            Updated = Palette.Ago(DateTimeOffset.FromUnixTimeMilliseconds((long?)s["updatedAt"] ?? 0)),
            Progress = total > 0 ? 100.0 * done / total : 0,
            ProgressVisibility = total > 0 && active ? Visibility.Visible : Visibility.Collapsed,
            StopVisibility = active ? Visibility.Visible : Visibility.Collapsed,
        };
    }

    public string Signature => $"{Id}|{Title}|{StateLabel}|{Detail}|{Progress}|{Updated}";
}

public sealed class PhoneItem
{
    public string Name { get; init; } = "";
    public string Route { get; init; } = "";
    public string Since { get; init; } = "";
}

public sealed class DeviceItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public SolidColorBrush DotBrush { get; init; } = Palette.State("");
}

public sealed class AgentItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Status { get; init; } = "";
    public SolidColorBrush StatusBrush { get; init; } = Palette.State("");
    public string PathText { get; init; } = "";
    public string Detail { get; init; } = "";
    public Visibility BrowseVisibility { get; init; } = Visibility.Visible;

    public static AgentItem From(AgentInfo a) => new()
    {
        Id = a.Id,
        Name = a.Name,
        Vendor = a.Vendor,
        Status = a.Available ? (a.Version is { Length: > 0 } v ? v : "Installed") : "Not found",
        StatusBrush = Palette.State(a.Available ? "idle" : "error"),
        PathText = a.Path ?? "",
        Detail = a.Detail,
        BrowseVisibility = a.Id == AgentCatalog.Echo ? Visibility.Collapsed : Visibility.Visible,
    };
}

public sealed class EndpointItem
{
    public string Url { get; init; } = "";
    public string Kind { get; init; } = "";
}
