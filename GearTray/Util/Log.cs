namespace GearTray.Util;

public static class Log
{
    private static readonly object Gate = new();

    /// <summary>%APPDATA%\GearTray: settings and log of a GearTray running without admin rights.</summary>
    public static string UserDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GearTray");

    /// <summary>
    /// Where settings and the log are kept. With admin rights (the installed GearTray, started by its logon task)
    /// that's C:\Program Files\GearTray\Data, which only administrators can change. %APPDATA%\GearTray can be changed
    /// by every program the user runs: one could swap the folder for a link and turn the log's writes and renames
    /// into writes and moves of other files with admin rights, or edit settings an elevated GearTray acts on (the
    /// macros it types). Without admin rights writing there is no such risk, so it stays %APPDATA%\GearTray.
    /// </summary>
    public static string Directory { get; } = Elevation.IsAdmin ? Elevation.DataDir : UserDirectory;
    public static string FilePath { get; } = Path.Combine(Directory, "log.txt");

    /// <summary>Also echo log lines to stdout (CLI mode).</summary>
    public static bool Echo { get; set; }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}: {ex.GetType().Name}: {ex.Message}");

    public static string Hex(ReadOnlySpan<byte> data, int max = 32) =>
        Convert.ToHexString(data[..Math.Min(max, data.Length)]) + (data.Length > max ? "…" : "");

    private static void Write(string level, string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (Exception ex) { if (Echo) Console.WriteLine($"(log file write failed: {ex.GetType().Name}: {ex.Message})"); }
            if (Echo) Console.WriteLine(line);
        }
    }
}
