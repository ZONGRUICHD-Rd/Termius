using System.Security.Cryptography;
using Conduit.Host.Core;
using Conduit.Host.Core.Security;
using Microsoft.UI.Xaml;

namespace Conduit.Host;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private MainWindow? _window;

    public static HostRuntime Runtime { get; private set; } = null!;
    public static MainWindow? Window { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Runtime?.Log("Unhandled: " + e.Exception.Message);
            CrashReporter.Report(e.Exception, "Unexpected error");
            e.Handled = true;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(true, "Conduit.Host.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Exit();
            return;
        }

        try
        {
            Runtime = new HostRuntime(HostRuntime.DefaultDataDirectory(), new DpapiProtector());
            _window = new MainWindow();
            Window = _window;
            _window.Activate();
            await Runtime.StartAsync();
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex, "Conduit Host could not start");
            Exit();
        }
    }
}

/// <summary>Encrypts the host's private keys at rest with the current Windows user's DPAPI key.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "conduit-host-v1"u8.ToArray();

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
