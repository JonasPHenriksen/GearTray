namespace GearTray.Util;

/// <summary>
/// "--uninstall", what Settings > Apps > Installed apps runs. It first asks whether the settings and log should go
/// too. Started without admin rights (the usual way), it then asks for them once (UAC) for the machine-wide part,
/// and deletes %APPDATA%\GearTray itself, with the user's own rights. Started with admin rights, it does the
/// machine-wide part and leaves %APPDATA%\GearTray alone: deleting in a folder every program of the user can change
/// is exactly what an elevated process shouldn't do.
/// </summary>
internal static class Uninstaller
{
    public const string Command = "--uninstall";
    private const string ElevatedStep = "--elevated-step"; // the UAC-started half; the other half talks to the user
    private const string DeleteData = "--delete-data";
    private const string Title = "Uninstall GearTray";

    public static int Run(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (Has(args, ElevatedStep))
        {
            if (!Elevation.IsAdmin) return 1;
            try
            {
                Elevation.UninstallMachineWide(Has(args, DeleteData));
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("Uninstall failed", ex);
                return 1;
            }
        }

        var choice = MessageBox.Show("GearTray will be removed.\n\nAlso delete your GearTray settings and log?",
            Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return 1;
        bool deleteData = choice == DialogResult.Yes;

        if (Elevation.IsAdmin)
        {
            try { Elevation.UninstallMachineWide(deleteData); }
            catch (Exception ex)
            {
                Log.Error("Uninstall failed", ex);
                Show("GearTray couldn't be removed completely: " + ex.Message, MessageBoxIcon.Error);
                return 1;
            }
            string note = deleteData && Directory.Exists(Log.UserDirectory)
                ? $"\n\nSettings from runs without admin rights are still in {Log.UserDirectory}. Delete that folder to remove them too."
                : "";
            Show(Removed(deleteData) + note, MessageBoxIcon.Information);
            return 0;
        }

        if (!Elevation.RunElevated($"{Command} {ElevatedStep}{(deleteData ? " " + DeleteData : "")}", wait: true))
        {
            Show("GearTray was not removed.", MessageBoxIcon.Warning);
            return 1;
        }
        if (deleteData && Directory.Exists(Log.UserDirectory))
        {
            // Not logged: a log line would create the folder again.
            try { Directory.Delete(Log.UserDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Show($"{Log.UserDirectory} couldn't be deleted: {ex.Message}", MessageBoxIcon.Warning);
            }
        }
        Show(Removed(deleteData), MessageBoxIcon.Information);
        return 0;
    }

    /// <summary>"GearTray has been removed." with what is left behind, looked up after the fact.</summary>
    private static string Removed(bool deleteData)
    {
        string text = "GearTray has been removed.";
        if (Directory.Exists(Elevation.InstallDir) && Directory.EnumerateFiles(Elevation.InstallDir, "*.old").Any())
            text += " One leftover file in Program Files is deleted at the next restart.";
        if (!deleteData && Directory.Exists(Elevation.DataDir))
            text += $"\n\nYour settings and log stay in {Elevation.DataDir} for a later install.";
        return text;
    }

    private static bool Has(string[] args, string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    private static void Show(string text, MessageBoxIcon icon) =>
        MessageBox.Show(text, Title, MessageBoxButtons.OK, icon);
}
