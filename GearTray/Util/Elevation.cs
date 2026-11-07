using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace GearTray.Util;

public static class Elevation
{
    public const string TaskName = "GearTray";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GearTray";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string existing, string? newName, int flags);
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

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
    /// <summary>Settings and log of a GearTray running with admin rights (see Log.Directory).</summary>
    public static string DataDir => Path.Combine(InstallDir, "Data");

    /// <summary>
    /// Copy this exe to Program Files (only admins can change files there, so nobody can swap the file the
    /// elevated task runs), add a Start menu shortcut and an entry in Settings > Apps > Installed apps, and register
    /// a logon task that runs it with highest privileges. Must be called from an elevated process. Task XML is used
    /// so there is no 72-hour limit.
    /// </summary>
    public static void InstallStartupTask()
    {
        Directory.CreateDirectory(InstallDir);
        if (!IsInstalledExe(ExePath))
        {
            if (!UI.DeviceChangeWindow.AskRunningInstanceToExit(10_000)) Log.Warn("The running GearTray didn't exit when asked");
            // The signed PawnIO module for CPU temperature travels next to the exe when it's there.
            string module = Path.Combine(Path.GetDirectoryName(ExePath)!, "AMDFamily17.bin");
            if (File.Exists(module)) File.Copy(module, Path.Combine(InstallDir, "AMDFamily17.bin"), overwrite: true);
            try { File.Copy(ExePath, InstalledExe, overwrite: true); }
            catch (IOException ex) { throw new IOException("Couldn't update GearTray in Program Files. Exit the running GearTray first.", ex); }
        }
        CreateStartMenuShortcut(InstalledExe);
        RegisterUninstallEntry();

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
        // Written to the install folder, not %TEMP%: any program of the user can change files in %TEMP% (and swap
        // the task definition before schtasks reads it), only administrators can in Program Files.
        string file = Path.Combine(InstallDir, "GearTray-task.xml");
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

    /// <summary>
    /// The machine-wide half of uninstalling (must run elevated): stops the running GearTray, then removes the
    /// logon task, the Start menu shortcut, the Installed apps entry and the files in Program Files, and the Data
    /// folder in there if <paramref name="deleteData"/>. A file that is still in use (GearTray.exe, which the
    /// uninstaller itself runs from) is renamed and left for Windows to delete at the next restart, together with
    /// the then empty folder. Returns true when that happened. %APPDATA%\GearTray is not touched here: deleting in a
    /// folder every program of the user can change is the non-elevated half's job, with the user's own rights.
    /// </summary>
    public static bool UninstallMachineWide(bool deleteData)
    {
        // Logs only when something goes wrong: every log line would (re)create the Data folder.
        if (!UI.DeviceChangeWindow.AskRunningInstanceToExit(10_000)) Log.Warn("The running GearTray didn't exit when asked");
        if (StartupTaskExists()) UninstallStartupTask();
        try { File.Delete(StartMenuShortcut); } catch (Exception ex) { Log.Warn("Start menu shortcut: " + ex.Message); }
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            hklm.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        bool afterRestart = false;
        if (Directory.Exists(InstallDir))
        {
            // Only administrators can create anything in this folder, so walking and deleting it is safe. A snapshot,
            // not a live enumeration: the renamed leftover below must not come up again as a new entry.
            foreach (var entry in new DirectoryInfo(InstallDir).GetFileSystemInfos())
            {
                if (!deleteData && string.Equals(entry.FullName, DataDir, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (entry is DirectoryInfo dir) dir.Delete(recursive: true);
                    else entry.Delete();
                }
                catch (Exception ex) when (entry is FileInfo && ex is IOException or UnauthorizedAccessException)
                {
                    // In use. A running exe can't be deleted but can be renamed: the leftover gets a name of its own,
                    // so a GearTray installed again before the restart isn't deleted with it (the folder then isn't
                    // empty at the restart and stays).
                    string leftover = Path.Combine(InstallDir, $"{entry.Name}.{Guid.NewGuid():N}.old");
                    File.Move(entry.FullName, leftover);
                    if (!MoveFileExW(leftover, null, MOVEFILE_DELAY_UNTIL_REBOOT))
                        throw new IOException($"Couldn't remove {entry.FullName}: {ex.Message}", ex);
                    afterRestart = true;
                }
            }
            if (!Directory.EnumerateFileSystemEntries(InstallDir).Any()) Directory.Delete(InstallDir);
            else if (afterRestart) MoveFileExW(InstallDir, null, MOVEFILE_DELAY_UNTIL_REBOOT); // after the file's entry
        }
        return afterRestart;
    }

    private static bool IsInstalledExe(string path) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "GearTray.lnk");

    /// <summary>The entry in Settings > Apps > Installed apps (and Control Panel's Programs and Features).</summary>
    private static void RegisterUninstallEntry()
    {
        var version = typeof(Elevation).Assembly.GetName().Version;
        long sizeKb = new DirectoryInfo(InstallDir).EnumerateFiles().Sum(f => f.Length) / 1024;
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "GearTray");
        key.SetValue("DisplayVersion", version?.ToString(3) ?? "");
        key.SetValue("Publisher", "Jonas Henriksen");
        key.SetValue("DisplayIcon", InstalledExe + ",0");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void CreateStartMenuShortcut(string target)
    {
        try
        {
            string lnk = StartMenuShortcut;
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
        // Full path: a bare "schtasks.exe" is looked up in the exe's folder and the current folder before System32.
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
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
