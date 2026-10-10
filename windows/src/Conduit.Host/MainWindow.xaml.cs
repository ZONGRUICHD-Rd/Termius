using Conduit.Host.Pages;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Conduit.Host;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueueTimer _refresh;
    // Must stay referenced: a DispatcherQueueTimer held only by a local is collected and silently stops ticking.
    private DispatcherQueueTimer? _smokeTimer;
    private bool _dirty = true;

    /// <summary>Last time the UI thread ran the refresh timer (smoke-test diagnostics).</summary>
    public static DateTime LastUiTick { get; private set; } = DateTime.MinValue;

    /// <summary>Pages subscribe to this; it fires on the UI thread at most ~5x per second.</summary>
    public event Action? RuntimeChanged;

    public MainWindow()
    {
        // Pages read App.Window as soon as they are navigated to, so publish it before anything navigates.
        App.Window = this;
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1120, 780));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "conduit.ico"));

        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(200);
        _refresh.Tick += (_, _) =>
        {
            LastUiTick = DateTime.UtcNow;
            if (!_dirty) return;
            _dirty = false;
            UpdateStatusChip();
            RuntimeChanged?.Invoke();
        };
        _refresh.Start();
        App.Runtime.Changed += () => _dirty = true;

        Activated += (_, _) => Program.WindowShown.Set();

        // Navigate once the window is fully constructed.
        DispatcherQueue.TryEnqueue(() => Nav.SelectedItem = Nav.MenuItems[0]);

        // CI smoke test: open every page once so a crash on any of them fails the build.
        if (Environment.GetEnvironmentVariable("CONDUIT_SMOKE") == "1")
        {
            new Thread(() =>
            {
                for (var i = 0; i < 60; i++)
                {
                    Thread.Sleep(3000);
                    var ago = LastUiTick == DateTime.MinValue ? "never" : $"{(DateTime.UtcNow - LastUiTick).TotalSeconds:F1}s ago";
                    App.Runtime?.Log($"Smoke heartbeat: UI timer last ran {ago}");
                }
            }) { IsBackground = true }.Start();
            var tour = new Queue<Action>(new Action[]
            {
                () => NavigateTo("pair"), () => NavigateTo("connection"), () => NavigateTo("agents"), () => NavigateTo("sessions"),
                () => Nav.SelectedItem = Nav.SettingsItem, () => NavigateTo("overview"),
            });
            var marker = Environment.GetEnvironmentVariable("CONDUIT_SMOKE_MARKER");
            var step = 0;
            var timer = _smokeTimer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (t, _) =>
            {
                step++;
                App.Runtime.Log($"Smoke tour step {step}");
                if (marker is { Length: > 0 }) File.WriteAllText(marker + ".progress", $"step {step}");
                if (tour.Count == 0)
                {
                    t.Stop();
                    App.Runtime.Log("Smoke tour complete");
                    if (marker is { Length: > 0 })
                        File.WriteAllText(marker, $"ok pid={Environment.ProcessId}");
                    return;
                }
                tour.Dequeue()();
            };
            timer.Start();
        }
        Closed += (_, _) =>
        {
            _refresh.Stop();
            // Stop agent processes before exiting; run off the UI thread to avoid a sync-context deadlock.
            Task.Run(() => App.Runtime.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(4));
        };
    }

    private void UpdateStatusChip()
    {
        var r = App.Runtime;
        var phones = r.Connections.Count;
        var running = r.Sessions.All.Count(s => Core.Protocol.SessionStates.IsActive(s.State));
        if (!r.DirectListening && r.RelayStatus != "online")
        {
            StatusDot.Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 0xBF, 0x4D, 0x43));
            StatusText.Text = r.DirectError is null ? "正在启动…" : "无法访问";
            return;
        }
        StatusDot.Fill = new SolidColorBrush(phones > 0 ? ColorHelper.FromArgb(255, 0x78, 0x8C, 0x5D) : ColorHelper.FromArgb(255, 0xD9, 0x77, 0x57));
        var parts = new List<string> { $"{phones} 台手机已连接" };
        if (running > 0) parts.Add($"{running} 个 Agent 工作中");
        StatusText.Text = string.Join(" · ", parts);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }
        var page = ((args.SelectedItem as NavigationViewItem)?.Tag as string) switch
        {
            "pair" => typeof(PairPage),
            "connection" => typeof(ConnectionPage),
            "agents" => typeof(AgentsPage),
            "sessions" => typeof(SessionsPage),
            _ => typeof(OverviewPage),
        };
        ContentFrame.Navigate(page);
    }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) p.Restore();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public void NavigateTo(string tag)
    {
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
    }
}
