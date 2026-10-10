using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Conduit.Host;

/// <summary>
/// Custom entry point so that every way the host can fail to start is visible to the user:
///  1. incomplete installs (e.g. resources.pri lost while unzipping) are detected before XAML loads;
///  2. a second launch brings the running window to the front instead of silently exiting, and a hung
///     background instance (no window) can be ended and replaced;
///  3. a watchdog reports and exits if no window appears, so no invisible zombie process is left behind.
/// </summary>
public static class Program
{
    public const string MutexName = "Conduit.Host.SingleInstance";
    public const string ActivateEventName = "Conduit.Host.Activate";
    public const string ActivatedAckEventName = "Conduit.Host.Activated";

    /// <summary>Set by MainWindow the first time it is shown.</summary>
    public static readonly ManualResetEventSlim WindowShown = new(false);

    private static Mutex? _instanceMutex;

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashReporter.Report(e.ExceptionObject as Exception, "发生未处理的错误");

        if (!CheckInstallation()) return 2;
        if (!AcquireSingleInstance()) return 0;
        StartWatchdog();

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_p =>
            {
                try
                {
                    var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    _ = new App();
                }
                catch (Exception ex)
                {
                    CrashReporter.Report(ex, "Conduit Host 无法启动");
                    Environment.Exit(1);
                }
            });
            return 0;
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex, "Conduit Host 无法启动");
            return 1;
        }
    }

    /// <summary>The unpackaged app needs these files next to the exe; a partial unzip is the most common failure.</summary>
    private static bool CheckInstallation()
    {
        var dir = AppContext.BaseDirectory;
        var required = new[] { "resources.pri", "Microsoft.ui.xaml.dll", "Microsoft.WindowsAppRuntime.dll", "ConduitHost.dll" };
        var missing = required.Where(f => !File.Exists(Path.Combine(dir, f))).ToList();
        if (missing.Count == 0) return true;
        CrashReporter.ShowMessage(
            "安装文件不完整",
            $"缺少以下文件：\n  {string.Join("\n  ", missing)}\n\n" +
            "请重新下载 ConduitHost 的 ZIP，并把整个压缩包完整解压到一个新文件夹后再运行 ConduitHost.exe。\n" +
            "不要只复制 ConduitHost.exe，也不要直接在压缩包里双击运行。\n\n" +
            $"当前目录：{dir}");
        CrashReporter.Log($"Installation incomplete, missing: {string.Join(", ", missing)}");
        return false;
    }

    private static bool AcquireSingleInstance()
    {
        _instanceMutex = new Mutex(false, MutexName);
        if (TryOwnMutex(TimeSpan.Zero)) return true;

        // Another instance holds the lock: ask it to show its window and wait for an acknowledgement.
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        using var ack = new EventWaitHandle(false, EventResetMode.AutoReset, ActivatedAckEventName);
        activate.Set();
        if (ack.WaitOne(TimeSpan.FromSeconds(4))) return false; // the running instance came to the front

        // It didn't answer: probably a hung process without a window. Offer to replace it.
        var others = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "ConduitHost"))
            .Where(p => p.Id != Environment.ProcessId).ToList();
        var pids = string.Join(", ", others.Select(p => p.Id));
        var replace = CrashReporter.AskYesNo(
            "Conduit Host 已在运行但没有响应",
            $"检测到另一个 Conduit Host 进程（PID {pids}）正在运行，但它没有显示窗口，也没有响应。\n\n是否结束它并重新启动？");
        if (!replace) return false;

        foreach (var p in others)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                CrashReporter.Log($"Could not end process {p.Id}: {ex.Message}");
            }
        }
        if (TryOwnMutex(TimeSpan.FromSeconds(5))) return true;
        CrashReporter.ShowMessage("无法启动 Conduit Host", "旧的进程无法结束。请在任务管理器中结束 ConduitHost.exe 后重试。");
        return false;
    }

    private static bool TryOwnMutex(TimeSpan timeout)
    {
        try
        {
            return _instanceMutex!.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true; // previous owner was killed; we own it now
        }
    }

    /// <summary>If no window shows up, report it and exit instead of lingering invisibly.</summary>
    private static void StartWatchdog()
    {
        var thread = new Thread(() =>
        {
            if (WindowShown.Wait(TimeSpan.FromSeconds(45))) return;
            CrashReporter.Report(new TimeoutException("主窗口在 45 秒内没有显示。"), "Conduit Host 界面初始化失败");
            Environment.Exit(3);
        })
        { IsBackground = true, Name = "Startup watchdog" };
        thread.Start();
    }
}

/// <summary>Writes %LOCALAPPDATA%\Conduit\crash.log and shows the error, so a failed start is never silent.</summary>
public static class CrashReporter
{
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Conduit", "crash.log");

    private static bool DialogsDisabled => Environment.GetEnvironmentVariable("CONDUIT_NO_DIALOG") == "1";

    public static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:u}  {text}\r\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Report(Exception? ex, string title)
    {
        Log($"{title}\r\n{ex}\r\n");
        ShowMessage(title, $"{ex?.GetType().Name}: {ex?.Message}\n\n详细信息已保存到：\n{LogPath}");
    }

    public static void ShowMessage(string title, string message)
    {
        if (DialogsDisabled) return;
        MessageBoxW(IntPtr.Zero, message, "Conduit Host — " + title, 0x10 /* MB_ICONERROR */ | 0x10000 /* MB_SETFOREGROUND */);
    }

    public static bool AskYesNo(string title, string message)
    {
        if (DialogsDisabled) return true;
        return MessageBoxW(IntPtr.Zero, message, "Conduit Host — " + title, 0x4 /* MB_YESNO */ | 0x30 /* MB_ICONWARNING */ | 0x10000) == 6 /* IDYES */;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
