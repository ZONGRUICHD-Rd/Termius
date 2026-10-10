using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Conduit.Host;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashReporter.Report(e.ExceptionObject as Exception, "Unhandled exception");
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                try
                {
                    var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    _ = new App();
                }
                catch (Exception ex)
                {
                    CrashReporter.Report(ex, "Conduit Host could not start");
                    Environment.Exit(1);
                }
            });
            return 0;
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex, "Conduit Host could not start");
            return 1;
        }
    }
}

/// <summary>Writes %LOCALAPPDATA%\Conduit\crash.log and shows the error, so a failed start is never silent.</summary>
public static class CrashReporter
{
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Conduit", "crash.log");

    public static void Report(Exception? ex, string title)
    {
        var text = $"{DateTime.Now:u}  {title}\r\n{ex}\r\n\r\n";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, text);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (Environment.GetEnvironmentVariable("CONDUIT_NO_DIALOG") == "1") return;
        MessageBoxW(IntPtr.Zero,
            $"{title}.\n\n{ex?.GetType().Name}: {ex?.Message}\n\nDetails were saved to:\n{LogPath}",
            "Conduit Host", 0x10 /* MB_ICONERROR */);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
