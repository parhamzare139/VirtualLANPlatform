using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
// UseWindowsForms pulls in rival FlowDirection and FontFamily types.
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Binding = System.Windows.Data.Binding;

namespace VirtualLANPlatform.UI.Localization;

public enum AppLanguage { Persian, English }

/// <summary>
/// The app's string table and current language.
///
/// <para>
/// Switching language has to take effect on screen immediately, with no restart, so
/// lookups go through an indexer that XAML binds to. Raising PropertyChanged for
/// <c>Item[]</c> invalidates every one of those bindings at once, which is what makes a
/// whole window re-read its text on a single call.
/// </para>
/// <para>
/// Layout direction and the UI font move with the language too: Persian is right-to-left
/// and uses the bundled Vazirmatn, English is left-to-right on Segoe UI. Both are exposed
/// as bindable properties for the same reason.
/// </para>
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    private AppLanguage _language = AppLanguage.Persian;

    private Loc() { }

    public AppLanguage Language => _language;

    /// <summary>Indexer bound from XAML. Unknown keys return the key itself so a missing
    /// translation shows up as an obvious token rather than an empty control.</summary>
    public string this[string key] => Strings.Get(_language, key);

    public FlowDirection Flow =>
        _language == AppLanguage.Persian ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>Opposite of <see cref="Flow"/>, for the few places that must mirror it.</summary>
    public FlowDirection FlowInverse =>
        _language == AppLanguage.Persian ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;

    public FontFamily Font => _language == AppLanguage.Persian
        ? new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Vazirmatn, Segoe UI, Tahoma")
        : new FontFamily("Segoe UI");

    public bool IsPersian => _language == AppLanguage.Persian;
    public bool IsEnglish => _language == AppLanguage.English;

    /// <summary>Raised after the language changes, for code-behind that has to re-render.</summary>
    public event Action? LanguageChanged;

    public void Set(AppLanguage language, bool persist = true)
    {
        if (_language == language) return;
        _language = language;
        if (persist) Save(language);

        // "Item[]" is WPF's signal that every indexer binding is stale.
        Notify("Item[]");
        Notify(nameof(Language));
        Notify(nameof(Flow));
        Notify(nameof(FlowInverse));
        Notify(nameof(Font));
        Notify(nameof(IsPersian));
        Notify(nameof(IsEnglish));
        LanguageChanged?.Invoke();
    }

    public void Toggle() => Set(_language == AppLanguage.Persian ? AppLanguage.English : AppLanguage.Persian);

    // ── Static helpers for code-behind ───────────────────────────────────────

    public static string T(string key) => I[key];

    /// <summary>Formatted lookup. Always uses the invariant culture so numbers and IPs
    /// render with ASCII digits in both languages — a Persian-digit IP is not usable.</summary>
    public static string T(string key, params object?[] args)
    {
        string format = I[key];
        try   { return string.Format(CultureInfo.InvariantCulture, format, args); }
        catch { return format; }
    }

    // ── Persistence and first-run detection ──────────────────────────────────

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VirtualLANPlatform", "language.txt");

    private static void Save(AppLanguage language)
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, language == AppLanguage.English ? "en" : "fa");
        }
        catch { }
    }

    /// <summary>The language the user last chose, or null if they never have.</summary>
    public static AppLanguage? LoadSaved()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            return File.ReadAllText(SettingsPath).Trim().ToLowerInvariant() switch
            {
                "en" => AppLanguage.English,
                "fa" => AppLanguage.Persian,
                _    => null
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Picks the opening language on a machine that has never chosen one: Persian for
    /// users in Iran, English for everyone else. An explicit choice always wins and is
    /// never second-guessed by this.
    /// </summary>
    public static async Task ApplyStartupLanguageAsync()
    {
        if (LoadSaved() is { } saved) { I.Set(saved, persist: false); return; }

        // Windows' own region is instant and right most of the time; start there so the
        // window never waits on the network to pick a direction to lay out in.
        I.Set(RegionLanguage(), persist: false);

        // Then refine from where the connection actually comes out. This is the case the
        // region check misses: an Iranian Windows install behind a foreign proxy or VPN
        // is someone who wants English.
        var geo = await GeoLanguageAsync().ConfigureAwait(false);
        if (geo is { } fromIp) I.Set(fromIp, persist: false);
    }

    private static AppLanguage RegionLanguage()
    {
        try
        {
            bool iranian =
                string.Equals(RegionInfo.CurrentRegion.TwoLetterISORegionName, "IR", StringComparison.OrdinalIgnoreCase) ||
                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "fa";
            return iranian ? AppLanguage.Persian : AppLanguage.English;
        }
        catch { return AppLanguage.Persian; }
    }

    /// <summary>
    /// Country of the address this machine actually reaches the internet from, which
    /// accounts for a proxy or VPN in a way the system region cannot. Null when it could
    /// not be determined — offline, blocked, or timed out — and the caller then keeps
    /// whatever the region check decided.
    /// </summary>
    private static async Task<AppLanguage?> GeoLanguageAsync()
    {
        // Two providers: these endpoints are exactly the sort of thing that gets blocked,
        // and the whole point is to work for someone whose connection is being filtered.
        string[] endpoints =
        [
            "https://ipapi.co/country/",
            "https://ifconfig.co/country-iso"
        ];

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VirtualLANPlatform");

        foreach (string url in endpoints)
        {
            try
            {
                string body = (await http.GetStringAsync(url).ConfigureAwait(false)).Trim();
                if (body.Length is < 2 or > 8) continue;
                string code = body[..2].ToUpperInvariant();
                return code == "IR" ? AppLanguage.Persian : AppLanguage.English;
            }
            catch { /* try the next one */ }
        }

        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// XAML shorthand: <c>Text="{loc:Tr Lobby_Create}"</c>.
/// Returns a live binding rather than a plain string, so the text follows a language
/// change without the window being rebuilt.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.I,
            Mode   = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
