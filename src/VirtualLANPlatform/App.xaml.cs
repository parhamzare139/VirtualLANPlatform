using System.Diagnostics;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox  = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;

namespace VirtualLANPlatform;

public partial class App : Application
{
    /// <summary>
    /// Launched by the logon task with --tray and the user asked to start hidden: the
    /// main window opens straight into the tray instead of on the desktop.
    /// </summary>
    public static bool StartHidden { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        StartHidden = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase))
                   && Core.Services.AppSettings.I.StartInTray;

        // Uninstall hook: the installer cannot remove a Wintun adapter on its own, so it
        // runs us with this switch first. Handle it before any UI exists and exit.
        if (e.Args.Any(a => string.Equals(a, "--remove-adapter", StringComparison.OrdinalIgnoreCase)))
        {
            RemoveVirtualAdapterAndExit();
            return;
        }

        // Language before any window is built: FlowDirection and the UI font are decided
        // at load, and starting Persian only to flip a moment later is a visible lurch.
        // The saved choice applies synchronously; the geo refinement lands behind it.
        if (UI.Localization.Loc.LoadSaved() is { } savedLanguage)
            UI.Localization.Loc.I.Set(savedLanguage, persist: false);
        else
            _ = UI.Localization.Loc.ApplyStartupLanguageAsync();

        // The update check runs from the main window's update gate, not here — it needs a
        // window to render into, and it gates the lobby rather than popping up behind it.
        _ = EnsureFirewallRuleAsync();

        DispatcherUnhandledException += (_, args) =>
        {
            Core.Services.AppLog.Error("app", "unhandled UI exception", args.Exception);
            MessageBox.Show(args.Exception.Message, UI.Localization.Loc.T("App_UnexpectedError"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Core.Services.AppLog.Error("app", "unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Core.Services.AppLog.Warn("app", $"unobserved task exception: {args.Exception.InnerException?.Message ?? args.Exception.Message}");
            args.SetObserved();
        };
    }

    /// <summary>
    /// Tears down the virtual network adapter, then quits. Runs headless: the uninstaller
    /// invokes it while removing the program, so there is no window and nothing to show.
    /// The outcome goes to a log beside the settings rather than a dialog — nobody is
    /// watching, and an error box would stall an unattended uninstall forever.
    /// </summary>
    private void RemoveVirtualAdapterAndExit()
    {
        string outcome;
        try   { outcome = Core.VirtualLan.WintunSession.RemoveAdapterAndDriver(); }
        catch (Exception ex) { outcome = $"unexpected: {ex.Message}"; }

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VirtualLANPlatform");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "uninstall.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {outcome}{Environment.NewLine}");
        }
        catch { }

        Shutdown(0);
    }

    // Add firewall rule so P2P UDP traffic is not blocked.
    // App runs as Administrator so this succeeds without a UAC prompt.
    //
    // Takes the actual port in use: called once at startup for the default port, and
    // again from CreateRoom_Click with whatever port the user actually configured —
    // a rule hardcoded to the default port would otherwise leave guests unable to
    // reach a host that changed their port, with no indication why the connection fails.
    public static Task EnsureFirewallRuleAsync(ushort port = 42777) => Task.Run(() =>
    {
        try
        {
            // Delete old rule first (ignore failure), then add fresh
            RunNetsh("advfirewall firewall delete rule name=\"VirtualLANPlatform UDP\"");
            RunNetsh(
                "advfirewall firewall add rule " +
                "name=\"VirtualLANPlatform UDP\" " +
                $"protocol=UDP dir=in localport={port} " +
                "action=allow profile=any");

            // Fixed UDP port for the standalone virtual-LAN transport.
            RunNetsh("advfirewall firewall delete rule name=\"VirtualLANPlatform VLAN\"");
            RunNetsh(
                "advfirewall firewall add rule " +
                "name=\"VirtualLANPlatform VLAN\" " +
                $"protocol=UDP dir=in localport={Core.VirtualLan.VirtualLanManager.VlanPort} " +
                "action=allow profile=any");
        }
        catch { /* non-fatal — app still works on LANs that don't need this */ }
    });

    private static void RunNetsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh", args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        })!;
        p.WaitForExit(3000);
    }
}
