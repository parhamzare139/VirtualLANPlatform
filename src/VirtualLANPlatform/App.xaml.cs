using System.Diagnostics;
using System.Windows;
using VirtualLANPlatform.Core.Services;
using Application = System.Windows.Application;
using MessageBox  = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;

namespace VirtualLANPlatform;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _ = EnsureFirewallRuleAsync();
        UpdateService.CheckOnStartup();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "خطای غیرمنتظره",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
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
