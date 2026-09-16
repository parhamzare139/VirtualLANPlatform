using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualLANPlatform.Core.Services;

/// <summary>
/// Everything the settings page edits, in one JSON file next to the other per-user
/// state. One file rather than the older one-value-per-file scheme so a new option is
/// a property here and nothing else; the legacy files (username, port, volume) stay
/// where they are because other code already reads them.
/// </summary>
public sealed class AppSettings
{
    public static AppSettings I { get; } = Load();

    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VirtualLANPlatform", "settings.json");

    // ── General ──────────────────────────────────────────────────────────────
    public bool RunAtStartup { get; set; }
    /// <summary>Start hidden in the tray (used with <see cref="RunAtStartup"/>, or the --tray switch).</summary>
    public bool StartInTray  { get; set; }
    /// <summary>The close button hides to the tray instead of quitting.</summary>
    public bool CloseToTray  { get; set; } = true;

    // ── Notifications & sounds ───────────────────────────────────────────────
    public bool WindowsNotifications { get; set; } = true;
    public bool Sounds        { get; set; } = true;
    public bool SoundJoinLeave{ get; set; } = true;
    public bool SoundMessage  { get; set; } = true;
    public bool SoundFile     { get; set; } = true;

    // ── Voice ────────────────────────────────────────────────────────────────
    public bool    PushToTalk     { get; set; }
    /// <summary>Virtual-key code. 0x04–0x06 are the middle / X1 / X2 mouse buttons.</summary>
    public int     PttKey         { get; set; } = 0x56; // V
    public string? InputDeviceId  { get; set; }
    public string? OutputDeviceId { get; set; }

    // ── Presence ─────────────────────────────────────────────────────────────
    /// <summary>Broadcast online / in-game / away to the people you are connected to.</summary>
    public bool ShareStatus { get; set; } = true;
    public int  AfkMinutes  { get; set; } = 5;

    // ── Bookkeeping ──────────────────────────────────────────────────────────
    /// <summary>Last version whose "what's new" the user has seen.</summary>
    public string LastSeenVersion { get; set; } = "";
    /// <summary>Set once the first close-to-tray hint has been shown.</summary>
    public bool TrayHintShown { get; set; }

    public event Action? Changed;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) { AppLog.Warn("settings", $"save failed: {ex.Message}"); }
        Changed?.Invoke();
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new AppSettings();
        }
        catch (Exception ex) { AppLog.Warn("settings", $"load failed, using defaults: {ex.Message}"); }
        return new AppSettings();
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented          = true,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        ReadCommentHandling    = JsonCommentHandling.Skip,
        AllowTrailingCommas    = true
    };
}
