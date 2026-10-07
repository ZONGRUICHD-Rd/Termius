using Conduit.Host.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Conduit.Host.Pages;

public sealed partial class PairPage : Page
{
    private string _link = "";
    private string _devicesSig = "";

    public PairPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        App.Window!.RuntimeChanged += Refresh;
        // Fresh code each time the page opens if the old one is stale.
        if (DateTimeOffset.UtcNow - App.Runtime.Config.PairingTokenCreated > TimeSpan.FromMinutes(25))
            App.Runtime.RegeneratePairingToken();
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.Window!.RuntimeChanged -= Refresh;

    private async void Refresh()
    {
        var r = App.Runtime;
        var link = r.PairingLink();
        if (link != _link)
        {
            _link = link;
            await RenderQr(link);
        }

        var expires = r.Config.PairingTokenCreated + HostRuntime.PairingTokenLifetime;
        var left = expires - DateTimeOffset.UtcNow;
        ExpiryText.Text = left > TimeSpan.Zero ? $"Valid for {Math.Ceiling(left.TotalMinutes)} more minutes" : "Expired — create a new code";

        var endpoints = r.Endpoints().Select(e => new EndpointItem { Kind = e.Kind, Url = e.Url }).ToList();
        if (!string.IsNullOrWhiteSpace(r.Config.RelayUrl))
            endpoints.Add(new EndpointItem { Kind = "relay", Url = r.Config.RelayUrl! });
        EndpointList.ItemsSource = endpoints;
        AnywhereHint.IsOpen = !endpoints.Any(e => e.Kind is "tailscale" or "relay" or "custom" or "public");

        var connected = r.ConnectedDeviceIds();
        var devices = r.Config.Devices.OrderByDescending(d => d.LastSeen ?? d.PairedAt).Select(d => new DeviceItem
        {
            Id = d.Id,
            Name = d.Name,
            Detail = connected.Contains(d.Id)
                ? "Connected now"
                : $"Paired {d.PairedAt.LocalDateTime:d MMM}" + (d.LastSeen is { } seen ? $" · last seen {Palette.Ago(seen)}" : ""),
            DotBrush = Palette.State(connected.Contains(d.Id) ? "idle" : "stopped"),
        }).ToList();
        var sig = string.Join(";", devices.Select(d => d.Id + d.Detail));
        if (sig != _devicesSig)
        {
            _devicesSig = sig;
            DeviceList.ItemsSource = devices;
        }
        NoDevices.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RenderQr(string link)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(12, [0x14, 0x14, 0x13, 0xFF], [0xFA, 0xF9, 0xF5, 0xFF]);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);
        QrImage.Source = bmp;
    }

    private void NewCode_Click(object sender, RoutedEventArgs e) => App.Runtime.RegeneratePairingToken();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(App.Runtime.PairingLink());
        Clipboard.SetContent(package);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id) return;
        var dialog = new ContentDialog
        {
            Title = "Remove this phone?",
            Content = "It will be disconnected and must scan a new QR code to pair again.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) App.Runtime.RemoveDevice(id);
    }
}
