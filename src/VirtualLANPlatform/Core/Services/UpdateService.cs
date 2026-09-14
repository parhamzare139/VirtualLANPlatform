using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Xml.Linq;

namespace VirtualLANPlatform.Core.Services;

/// <summary>What a check found. <see cref="Available"/> is the only state that offers a download.</summary>
public enum UpdateState
{
    /// <summary>Running version matches (or beats) the published one.</summary>
    UpToDate,
    /// <summary>A newer version is published and downloadable.</summary>
    Available,
    /// <summary>The manifest couldn't be reached or parsed — treated as "carry on".</summary>
    Failed
}

public sealed record UpdateInfo(
    UpdateState State,
    Version?    Latest    = null,
    Version?    Current   = null,
    string?     Url       = null,
    string?     Changelog = null,
    bool        Mandatory = false,
    string?     Error     = null);

/// <summary>
/// Checks the hosted XML manifest for a newer build, downloads the installer and runs it.
/// <para>
/// This replaced AutoUpdater.NET's own dialogs: those are WinForms, render left-to-right
/// in English, and ignore the app's theme entirely. The UI now lives in the app (see the
/// update gate in TestWindow) and this class is pure logic — no windows of its own.
/// </para>
/// </summary>
public static class UpdateService
{
    private const string ManifestUrl =
        "https://raw.githubusercontent.com/parhamzare139/VirtualLANPlatform/main/update.xml";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>The running build, from the assembly version.</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>
    /// Fetches and parses the manifest. Never throws — a network failure comes back as
    /// <see cref="UpdateState.Failed"/> so a machine that's offline can still use the app.
    /// </summary>
    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            // Bust any CDN/proxy cache: raw.githubusercontent is aggressively cached and a
            // stale copy would hide a release that's already out.
            string url = $"{ManifestUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            string xml = await Http.GetStringAsync(url, ct);

            var item = XDocument.Parse(xml).Root;
            if (item == null)
                return new UpdateInfo(UpdateState.Failed, Error: UI.Localization.Loc.T("Upd_EmptyBody"));

            string? versionText = item.Element("version")?.Value.Trim();
            if (!Version.TryParse(versionText, out var latest))
                return new UpdateInfo(UpdateState.Failed, Error: UI.Localization.Loc.T("Upd_BadVersion"));

            var current = CurrentVersion;

            // Compare only Major.Minor.Build: the manifest publishes three parts while the
            // assembly carries four, so a straight Version comparison would always report
            // 1.0.7 > 1.0.7.0 and loop the user through an update they already have.
            var latestCore  = new Version(latest.Major,  latest.Minor,  Math.Max(latest.Build, 0));
            var currentCore = new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

            string? downloadUrl = item.Element("url")?.Value.Trim();
            bool mandatory      = bool.TryParse(item.Element("mandatory")?.Value.Trim(), out bool m) && m;

            if (latestCore <= currentCore || string.IsNullOrWhiteSpace(downloadUrl))
                return new UpdateInfo(UpdateState.UpToDate, latestCore, currentCore);

            return new UpdateInfo(
                UpdateState.Available,
                latestCore,
                currentCore,
                downloadUrl,
                item.Element("changelog")?.Value.Trim(),
                mandatory);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new UpdateInfo(UpdateState.Failed, Error: ex.Message);
        }
    }

    /// <summary>
    /// Downloads the installer to a temp file, reporting 0-100 progress.
    /// Returns the path, or null if the download failed.
    /// </summary>
    public static async Task<string?> DownloadAsync(
        string url, IProgress<double> progress, CancellationToken ct = default)
    {
        string dir  = Path.Combine(Path.GetTempPath(), "VirtualLANPlatformUpdate");
        string path = Path.Combine(dir, "VirtualLANPlatform_Setup.exe");

        try
        {
            Directory.CreateDirectory(dir);
            // A half-finished file from a previous attempt would otherwise be appended to.
            if (File.Exists(path)) File.Delete(path);

            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long   total = resp.Content.Headers.ContentLength ?? -1;
            long   done  = 0;
            byte[] buf   = new byte[81920];

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);

            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                // Unknown length: crawl toward 90% so the bar still moves.
                progress.Report(total > 0
                    ? done * 100.0 / total
                    : Math.Min(90, done / 1024.0 / 1024.0 * 10));
            }

            progress.Report(100);
            return path;
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Launches the installer elevated and returns true if it started. The caller is
    /// expected to shut the app down immediately — the installer can't replace files
    /// that this process still holds open.
    /// </summary>
    public static bool RunInstaller(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true,
                Verb            = "runas"
            });
            return true;
        }
        catch { return false; }
    }
}
