using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Conduit.Host.Pages;

public sealed partial class ConnectionPage : Page
{
    public ConnectionPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var c = App.Runtime.Config;
        LanToggle.IsOn = c.LanEnabled;
        CfToggle.IsOn = c.Cloudflare.Enabled;
        CfMode.SelectedIndex = c.Cloudflare.Mode == "token" ? 1 : 0;
        CfToken.Password = c.Cloudflare.Token;
        CfHostname.Text = c.Cloudflare.Hostname;
        DdnsToggle.IsOn = c.Ddns.Enabled;
        DdnsDomain.Text = c.Ddns.Domain;
        DdnsPort.Value = c.Ddns.Port;
        DdnsTls.IsChecked = c.Ddns.Tls;
        RelayBox.Text = c.RelayUrl ?? "";
        ExtraBox.Text = string.Join("\r", c.ExtraEndpoints);
        CfTokenPanel.Visibility = CfMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        App.Window!.RuntimeChanged += Refresh;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.Window!.RuntimeChanged -= Refresh;

    private void Refresh()
    {
        var r = App.Runtime;
        LanAddresses.Text = string.Join("\n", r.Endpoints().Where(x => x.Kind is "lan" or "tailscale").Select(x => $"{x.Kind}: {x.Url}"));
        (CfStatus.Text, CfDot.Fill) = r.TunnelStatus switch
        {
            "online" => ("已连接 Cloudflare", Palette.State("idle")),
            "starting" => (r.TunnelError ?? "正在启动…", Palette.State("waiting")),
            "error" => ("出错：" + (r.TunnelError ?? "未知错误"), Palette.State("error")),
            _ => ("未开启", Palette.State("stopped")),
        };
        CfUrl.Text = r.TunnelUrl is { } url ? "外网地址：" + url : "";
    }

    private void CfMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CfTokenPanel is not null)
            CfTokenPanel.Visibility = CfMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var relay = RelayBox.Text.Trim();
        if (relay.Length > 0 && !relay.StartsWith("ws://") && !relay.StartsWith("wss://")) relay = "wss://" + relay;
        App.Runtime.ApplyConnectionSettings(new JsonObject
        {
            ["lan"] = LanToggle.IsOn,
            ["cloudflare"] = new JsonObject
            {
                ["enabled"] = CfToggle.IsOn,
                ["mode"] = CfMode.SelectedIndex == 1 ? "token" : "quick",
                ["token"] = CfToken.Password,
                ["hostname"] = CfHostname.Text,
            },
            ["ddns"] = new JsonObject
            {
                ["enabled"] = DdnsToggle.IsOn,
                ["domain"] = DdnsDomain.Text,
                ["port"] = double.IsNaN(DdnsPort.Value) ? 47821 : (int)DdnsPort.Value,
                ["tls"] = DdnsTls.IsChecked == true,
            },
            ["relay"] = relay,
        });
        App.Runtime.Update(c => c.ExtraEndpoints = ExtraBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
        SavedText.Text = "已保存 ✓ 已配对的手机会自动获得新地址";
        Refresh();
    }
}
