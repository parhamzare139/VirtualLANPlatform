using AutoUpdaterDotNET;
using System.Windows;
using System.Windows.Threading;

namespace VirtualLANPlatform.Core.Services;

/// <summary>
/// Wraps AutoUpdater.NET — checks for new versions from the hosted XML manifest.
/// Call CheckOnStartup() once at app start and CheckNow() on manual request.
/// </summary>
public static class UpdateService
{
    // Replace this URL after publishing the GitHub repository.
    private const string ManifestUrl =
        "https://raw.githubusercontent.com/your-org/VirtualLANPlatform/main/update.xml";

    private static bool _configured;

    private static void Configure()
    {
        if (_configured) return;
        _configured = true;

        AutoUpdater.AppTitle        = "Virtual LAN Platform";
        AutoUpdater.RunUpdateAsAdmin = true;
        AutoUpdater.ShowSkipButton   = true;
        AutoUpdater.ShowRemindLaterButton = true;

        // Don't show "up to date" dialog on automatic background checks
        AutoUpdater.ReportErrors = false;
    }

    /// <summary>Silent background check — runs 5 seconds after startup.</summary>
    public static void CheckOnStartup()
    {
        Configure();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            AutoUpdater.Start(ManifestUrl);
        };
        timer.Start();
    }

    /// <summary>Manual check triggered by user — shows "up to date" dialog too.</summary>
    public static void CheckNow()
    {
        Configure();
        AutoUpdater.ReportErrors = true;
        AutoUpdater.Start(ManifestUrl);
        AutoUpdater.ReportErrors = false;
    }
}
