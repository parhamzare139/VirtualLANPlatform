using System.Diagnostics;

namespace VirtualLANPlatform.Core.Services;

/// <summary>
/// "Run at Windows startup", done through the Task Scheduler rather than the usual
/// HKCU\...\Run key. The app's manifest demands administrator rights, and Windows
/// silently refuses to launch elevated programs from the Run key at logon (they show
/// up as "blocked" in the tray and never start). A logon task flagged to run with
/// highest privileges starts without a UAC prompt, which is the whole point.
/// </summary>
public static class StartupRegistration
{
    private const string TaskName = "VirtualLANPlatform";

    public static bool IsEnabled()
        => Schtasks($"/query /tn \"{TaskName}\"") == 0;

    public static bool Enable()
    {
        string? exe = Environment.ProcessPath;
        if (exe is not { Length: > 0 }) return false;
        // /rl highest: run elevated. No /ru: runs only when this user is logged on, in
        // the interactive session — exactly what a tray app needs.
        int rc = Schtasks($"/create /f /sc onlogon /rl highest /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" --tray\"");
        AppLog.Info("startup", rc == 0 ? "logon task created" : $"schtasks /create failed ({rc})");
        return rc == 0;
    }

    public static void Disable()
    {
        int rc = Schtasks($"/delete /f /tn \"{TaskName}\"");
        AppLog.Info("startup", rc == 0 ? "logon task removed" : $"schtasks /delete returned {rc}");
    }

    private static int Schtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks", args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            })!;
            return p.WaitForExit(8000) ? p.ExitCode : -1;
        }
        catch { return -1; }
    }
}
