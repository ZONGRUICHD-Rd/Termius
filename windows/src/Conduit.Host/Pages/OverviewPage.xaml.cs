using Conduit.Host.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Conduit.Host.Pages;

public sealed partial class OverviewPage : Page
{
    private string _activeSig = "";
    private string _phonesSig = "";

    public OverviewPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        App.Window!.RuntimeChanged += Refresh;
        App.Runtime.LogAdded += OnLog;
        Refresh();
        RefreshLog();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Window!.RuntimeChanged -= Refresh;
        App.Runtime.LogAdded -= OnLog;
    }

    private void OnLog(string _) => DispatcherQueue.TryEnqueue(RefreshLog);

    private void RefreshLog()
    {
        LogText.Text = string.Join("\n", App.Runtime.RecentLog.TakeLast(120));
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight + 1000, null, true);
    }

    private void Refresh()
    {
        var r = App.Runtime;

        DirectValue.Text = r.DirectListening ? $"Port {r.Config.Port}" : "Offline";
        var endpoints = r.Endpoints();
        DirectDetail.Text = r.DirectListening
            ? (endpoints.Count == 0 ? "No network" : string.Join("\n", endpoints.Take(3).Select(e => $"{e.Kind}: {new Uri(e.Url).Host}")))
            : r.DirectError ?? "Starting…";

        (RelayValue.Text, RelayDetail.Text) = r.RelayStatus switch
        {
            "online" => ("Relay online", r.Config.RelayUrl ?? ""),
            "connecting" => ("Connecting…", r.Config.RelayUrl ?? ""),
            "offline" => ("Relay offline", r.RelayError ?? r.Config.RelayUrl ?? ""),
            _ => endpoints.Any(e => e.Kind == "tailscale")
                ? ("Tailscale", "Reachable from anywhere on your tailnet")
                : ("LAN only", "Add a relay or install Tailscale to connect from anywhere"),
        };

        var conns = r.Connections.Where(c => c.Device is not null).ToList();
        PhonesValue.Text = conns.Count.ToString();
        PhonesDetail.Text = $"{r.Config.Devices.Count} paired";

        var agents = r.Agents.List();
        AgentsValue.Text = agents.Count(a => a.Available).ToString();
        AgentsDetail.Text = string.Join(", ", agents.Where(a => a.Available).Select(a => a.Name)) is { Length: > 0 } names ? names : "None detected";

        SetupHint.IsOpen = r.Config.Devices.Count == 0;

        var active = r.Sessions.All.Select(s => s.Summary())
            .Where(s => SessionStates.IsActive((string?)s["state"] ?? ""))
            .OrderByDescending(s => (long?)s["updatedAt"])
            .Select(SessionItem.From).ToList();
        var sig = string.Join(";", active.Select(a => a.Signature));
        if (sig != _activeSig)
        {
            _activeSig = sig;
            ActiveList.ItemsSource = active;
        }
        NoActive.Visibility = active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var phones = conns.Select(c => new PhoneItem
        {
            Name = c.Device!.Name,
            Route = c.Route switch { "relay" => "via relay", "tailscale" => "via Tailscale", _ => $"direct ({c.RemoteAddress})" },
            Since = "since " + c.ConnectedAt.LocalDateTime.ToString("t"),
        }).ToList();
        var psig = string.Join(";", phones.Select(p => p.Name + p.Route));
        if (psig != _phonesSig)
        {
            _phonesSig = psig;
            PhonesList.ItemsSource = phones;
        }
        NoPhones.Visibility = phones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Headline.Text = active.Count > 0 ? (active.Count == 1 ? "1 agent at work" : $"{active.Count} agents at work") : "All quiet";
    }

    private void Pair_Click(object sender, RoutedEventArgs e) => App.Window?.NavigateTo("pair");
}
