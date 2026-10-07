using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Conduit.Host.Pages;

public sealed partial class SettingsPage : Page
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private string? _pendingKey;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var c = App.Runtime.Config;
        HostNameBox.Text = c.HostName;
        PortBox.Value = c.Port;
        RelayBox.Text = c.RelayUrl ?? "";
        ExtraBox.Text = string.Join("\r", c.ExtraEndpoints);
        ApnsToggle.IsOn = c.Apns.Enabled;
        TeamBox.Text = c.Apns.TeamId;
        KeyIdBox.Text = c.Apns.KeyId;
        KeyStatus.Text = string.IsNullOrEmpty(c.Apns.PrivateKeyPem) ? "No key loaded" : "Key loaded";
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        StartupToggle.IsOn = key?.GetValue("ConduitHost") is not null;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var r = App.Runtime;
        var oldPort = r.Config.Port;
        var oldRelay = r.Config.RelayUrl;
        var relay = RelayBox.Text.Trim();
        if (relay.Length > 0 && !relay.StartsWith("ws://") && !relay.StartsWith("wss://")) relay = "wss://" + relay;

        r.Update(c =>
        {
            c.HostName = string.IsNullOrWhiteSpace(HostNameBox.Text) ? Environment.MachineName : HostNameBox.Text.Trim();
            if (!double.IsNaN(PortBox.Value)) c.Port = (int)PortBox.Value;
            c.RelayUrl = relay.Length == 0 ? null : relay;
            c.ExtraEndpoints = ExtraBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            c.Apns.Enabled = ApnsToggle.IsOn;
            c.Apns.TeamId = TeamBox.Text.Trim();
            c.Apns.KeyId = KeyIdBox.Text.Trim();
            if (_pendingKey is not null) c.Apns.PrivateKeyPem = _pendingKey;
        });
        _pendingKey = null;

        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (StartupToggle.IsOn) key.SetValue("ConduitHost", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("ConduitHost", throwOnMissingValue: false);
        }

        if (r.Config.Port != oldPort) await r.RestartDirectAsync();
        if (r.Config.RelayUrl != oldRelay) r.RestartRelay();
        SavedText.Text = "Saved ✓";
    }

    private async void LoadKey_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".p8");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        _pendingKey = await FileIO.ReadTextAsync(file);
        // AuthKey_ABC123XYZ.p8 → key id
        var name = Path.GetFileNameWithoutExtension(file.Name);
        if (name.StartsWith("AuthKey_") && string.IsNullOrWhiteSpace(KeyIdBox.Text)) KeyIdBox.Text = name["AuthKey_".Length..];
        KeyStatus.Text = $"{file.Name} (save to apply)";
    }

    private void Firewall_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath ?? "";
        var args = $"advfirewall firewall add rule name=\"Conduit Host\" dir=in action=allow program=\"{exe}\" enable=yes profile=private,domain";
        try
        {
            Process.Start(new ProcessStartInfo("netsh", args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // user cancelled UAC
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", App.Runtime.DataDirectory) { UseShellExecute = true });
}
