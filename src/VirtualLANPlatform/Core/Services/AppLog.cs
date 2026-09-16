using System.IO;
using System.IO.Compression;
using System.Text;

namespace VirtualLANPlatform.Core.Services;

/// <summary>
/// Plain-text application log, one file per day under the per-user data folder.
/// Nothing fancy on purpose: it exists so that when a user says "it doesn't work"
/// there is something to read, and so the bug-report bundle has content.
/// </summary>
public static class AppLog
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VirtualLANPlatform", "logs");

    private static readonly object _lock = new();
    private const int KeepDays = 7;

    public static void Info (string area, string message) => Write("INFO ", area, message);
    public static void Warn (string area, string message) => Write("WARN ", area, message);
    public static void Error(string area, string message, Exception? ex = null)
        => Write("ERROR", area, ex == null ? message : $"{message} — {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string area, string message)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {area}: {message}{Environment.NewLine}";
            lock (_lock)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}.log"), line, Encoding.UTF8);
            }
        }
        catch { /* logging must never take the app down */ }
    }

    /// <summary>Deletes log files older than a week. Call once at startup.</summary>
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.GetFiles(Dir, "app-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays))
                    File.Delete(f);
        }
        catch { }
    }

    /// <summary>
    /// Bundles every log file plus a diagnostics text into a zip on the desktop and
    /// returns its path. The caller supplies the diagnostics because most of what is
    /// worth knowing (adapters, firewall, router) lives in the UI layer's probes.
    /// </summary>
    public static string CreateReportZip(string diagnostics)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string zipPath = Path.Combine(desktop, $"VirtualLAN-Report-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var diag = zip.CreateEntry("diagnostics.txt");
        using (var w = new StreamWriter(diag.Open(), new UTF8Encoding(false)))
            w.Write(diagnostics);

        if (Directory.Exists(Dir))
        {
            foreach (var f in Directory.GetFiles(Dir, "*.log"))
            {
                // Logs are still open for append; copy through a shared-read stream.
                var entry = zip.CreateEntry("logs/" + Path.GetFileName(f));
                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
        }

        string settings = AppSettings.Path;
        if (File.Exists(settings)) zip.CreateEntryFromFile(settings, "settings.json");

        return zipPath;
    }
}
