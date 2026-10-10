using System.Security.Cryptography;
using Conduit.Host.Core;
using Conduit.Host.Core.Security;
using Microsoft.UI.Xaml;

namespace Conduit.Host;

public partial class App : Application
{
    private MainWindow? _window;

    public static HostRuntime Runtime { get; private set; } = null!;
    public static MainWindow? Window { get; internal set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Runtime?.Log("Unhandled: " + e.Exception.Message);
            CrashReporter.Report(e.Exception, "发生意外错误");
            e.Handled = true;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Runtime = new HostRuntime(HostRuntime.DefaultDataDirectory(), new DpapiProtector());
            _window = new MainWindow();
            Window = _window;
            _window.Activate();
            ListenForActivation(_window);
            await Runtime.StartAsync();
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex, "Conduit Host 无法启动");
            // Exit the whole process so no invisible instance keeps the single-instance lock.
            Environment.Exit(1);
        }
    }

    /// <summary>A second launch signals this event; bring the existing window to the front and acknowledge.</summary>
    private static void ListenForActivation(MainWindow window)
    {
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEventName);
        var ack = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivatedAckEventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                activate.WaitOne();
                window.DispatcherQueue.TryEnqueue(() =>
                {
                    window.BringToFront();
                    ack.Set();
                });
            }
        })
        { IsBackground = true, Name = "Activation listener" };
        thread.Start();
    }
}

/// <summary>Encrypts the host's private keys at rest with the current Windows user's DPAPI key.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "conduit-host-v1"u8.ToArray();

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
