using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace GearTray.Util;

public static class Elevation
{
    public const string TaskName = "GearTray";

    public static bool IsAdmin
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>Relaunch this exe elevated (UAC prompt) with the given arguments. Returns false if the user declined.</summary>
    public static bool RunElevated(string args, bool wait)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(ExePath, args) { UseShellExecute = true, Verb = "runas" });
            if (wait) p?.WaitForExit();
            return p is not null && (!wait || p.ExitCode == 0);
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // UAC cancelled
    }

    public static bool StartupTaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    /// <summary>Start the elevated instance through the scheduled task (no UAC prompt).</summary>
    public static bool RunStartupTask() => RunSchtasks($"/Run /TN \"{TaskName}\"", out _) == 0;

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GearTray");
    public static string InstalledExe => Path.Combine(InstallDir, "GearTray.exe");

    /// <summary>
    /// Copy this exe to Program Files (only admins can change files there, so nobody can swap the file the
    /// elevated task runs), add a Start menu shortcut, and register a logon task that runs it with highest
    /// privileges. Must be called from an elevated process. Task XML is used so there is no 72-hour limit.
    /// </summary>
    public static void InstallStartupTask()
    {
        if (!string.Equals(Path.GetFullPath(ExePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(InstallDir);
            try { File.Copy(ExePath, InstalledExe, overwrite: true); }
            catch (IOException ex) { throw new IOException("Couldn't update GearTray in Program Files. Exit the running GearTray first.", ex); }
        }
        CreateStartMenuShortcut(InstalledExe);

        string user = WindowsIdentity.GetCurrent().Name;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>GearTray: control for gaming peripherals. Runs with admin rights so the Razer keyboard can be controlled.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Escape(user)}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{Escape(InstalledExe)}</Command><Arguments>--startup</Arguments></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), "GearTray-task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            int code = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", out string output);
            if (code != 0) throw new InvalidOperationException($"schtasks failed ({code}): {output}");
        }
        finally { File.Delete(file); }
    }

    public static void UninstallStartupTask()
    {
        int code = RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out string output);
        if (code != 0 && StartupTaskExists()) throw new InvalidOperationException($"schtasks failed ({code}): {output}");
    }

    private static void CreateStartMenuShortcut(string target)
    {
        try
        {
            string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "GearTray.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(lnk);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = "Battery, settings and lighting for your mouse, keyboard and headset";
            link.Save();
        }
        catch (Exception ex) { Log.Warn("Start menu shortcut: " + ex.Message); }
    }

    private static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    private static int RunSchtasks(string args, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
