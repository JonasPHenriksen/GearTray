using GearTray.UI;
using GearTray.Util;

namespace GearTray;

internal static class Program
{
    private static Mutex? _instance;

    [STAThread]
    private static int Main(string[] args)
    {
        // --out FILE: send CLI output to a file (elevated runs can't redirect stdout).
        int oi = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
        StreamWriter? outFile = null;
        if (oi >= 0 && oi + 1 < args.Length)
        {
            outFile = new StreamWriter(args[oi + 1], append: false) { AutoFlush = true };
            Console.SetOut(outFile);
            args = args.Where((_, i) => i != oi && i != oi + 1).ToArray();
        }

        try
        {
            if (args.Length > 0 && args[0].Equals(Uninstaller.Command, StringComparison.OrdinalIgnoreCase))
                return Uninstaller.Run(args);

            if (args.Length > 0 && Cli.Commands.Contains(args[0].ToLowerInvariant()))
                return Cli.Run(args);

            if (args.Length > 1 && args[0].Equals("--screenshot", StringComparison.OrdinalIgnoreCase))
                return Screenshot(args[1]);

            return RunTray(fromStartupTask: args.Contains("--startup", StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            outFile?.Dispose();
        }
    }

    private static int RunTray(bool fromStartupTask)
    {
        // A launch that only hands over (to the running GearTray or to the startup task) logs nothing: without admin
        // rights its log would be %APPDATA%\GearTray, a folder the installed GearTray doesn't otherwise use.
        if (!AcquireInstanceLock())
        {
            DeviceChangeWindow.SignalRunningInstance(); // already running: just open its settings window
            return 0;
        }

        // Started by hand without admin rights: prefer the elevated instance via the startup task (no UAC prompt).
        if (!fromStartupTask && !Elevation.IsAdmin && Elevation.StartupTaskExists())
        {
            ReleaseInstanceLock();
            if (Elevation.RunStartupTask()) return 0;
            if (!AcquireInstanceLock()) return 0;
        }

        Log.Info($"startup: tray mode, from task {fromStartupTask}, admin {Elevation.IsAdmin}, session {System.Diagnostics.Process.GetCurrentProcess().SessionId}, exe {Environment.ProcessPath}");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI error", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal error", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Log.Info("startup: UI initialized");
        Application.Run(new TrayContext());
        ReleaseInstanceLock();
        return 0;
    }

    /// <summary>Diagnostics: open the settings window off-screen, render each tab to DIR\tab-*.png, exit.</summary>
    private static int Screenshot(string dir)
    {
        ApplicationConfiguration.Initialize();
        var tray = new TrayContext();
        var form = new SettingsForm(tray) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
        form.Shown += async (_, _) =>
        {
            try { await form.CaptureTabsAsync(dir); }
            catch (Exception ex) { Log.Error("Screenshot", ex); }
            tray.ExitThread();
        };
        form.Show();
        Application.Run(tray);
        return 0;
    }

    public static bool AcquireInstanceLock()
    {
        try
        {
            var m = new Mutex(initiallyOwned: true, @"Local\GearTray.Instance", out bool created);
            if (!created) { m.Dispose(); return false; }
            _instance = m;
            return true;
        }
        catch (UnauthorizedAccessException) { return false; } // an elevated instance owns it
    }

    public static void ReleaseInstanceLock()
    {
        if (_instance is null) return;
        try { _instance.ReleaseMutex(); } catch (ApplicationException) { }
        _instance.Dispose();
        _instance = null;
    }
}
