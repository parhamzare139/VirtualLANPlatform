using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using VirtualLANPlatform.UI.Emoji;

namespace VirtualLANPlatform.UI;

/// <summary>Takes the leading character of a name for use as an avatar monogram.</summary>
public sealed class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string s = value as string ?? "";
        s = s.TrimStart();
        return s.Length == 0 ? Localization.Loc.T("Member_Unknown") : s[..1].ToUpperInvariant();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Turns a plain-text chat message into WPF inlines: clickable Hyperlinks for URLs,
/// bundled 3D images for emoji, plain Runs for everything else.
/// Shared by the FlowDocument path and the TextBlock path so both render identically.
/// </summary>
public static class MessageInlines
{
    private static readonly Regex UrlRx = new(
        @"https?://[^\s<>""{}|\\^`\[\]]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static void Build(InlineCollection target, string text)
    {
        int pos = 0;
        foreach (Match m in UrlRx.Matches(text))
        {
            if (m.Index > pos)
                AddTextWithEmoji(target, text[pos..m.Index]);

            string url    = m.Value;
            string? emoji = PlatformEmoji(url);
            if (emoji != null)
                target.Add(new Run(emoji + " ") { FontSize = 11 });

            try
            {
                var linkColor = System.Windows.Media.Color.FromRgb(0x60, 0xA5, 0xFA);
                var link = new Hyperlink(new Run(url))
                {
                    NavigateUri     = new Uri(url),
                    Foreground      = new System.Windows.Media.SolidColorBrush(linkColor),
                    TextDecorations = null,
                    Cursor          = System.Windows.Input.Cursors.Hand
                };
                link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
                link.MouseLeave += (_, _) => link.TextDecorations = null;
                target.Add(link);
            }
            catch { target.Add(new Run(url)); }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            AddTextWithEmoji(target, text[pos..]);
    }

    /// <summary>Splits <paramref name="text"/> into runs, swapping in a bundled 3D image wherever a known emoji appears.</summary>
    private static void AddTextWithEmoji(InlineCollection target, string text)
    {
        int i = 0, runStart = 0;
        while (i < text.Length)
        {
            if (EmojiTextScanner.TryMatch(text, i, out string emoji) &&
                Emoji3DImages.TryGet(emoji, out var image) && image != null)
            {
                if (i > runStart)
                    target.Add(new Run(text[runStart..i]));

                var img = EmojiInline.CreateImage(image, emoji, 18);
                target.Add(new InlineUIContainer(img) { BaselineAlignment = BaselineAlignment.Center });

                i        += emoji.Length;
                runStart =  i;
            }
            else i++;
        }

        if (runStart < text.Length)
            target.Add(new Run(text[runStart..]));
    }

    private static string? PlatformEmoji(string url)
    {
        if (url.Contains("youtube.com") || url.Contains("youtu.be"))        return "▶";
        if (url.Contains("github.com"))                                       return "🐙";
        if (url.Contains("instagram.com"))                                    return "📷";
        if (url.Contains("t.me/") || url.Contains("telegram.org") ||
            url.Contains("telegram.me"))                                      return "✈";
        if (url.Contains("discord.com") || url.Contains("discord.gg"))       return "💬";
        if (url.Contains("twitter.com") || url.Contains("x.com"))            return "🐦";
        return null;
    }
}

/// <summary>
/// Populates a TextBlock's Inlines from a plain-text message.
/// <para>
/// Chat bubbles use a TextBlock rather than a RichTextBox because a RichTextBox always
/// reports the full available width as its desired width — every bubble would stretch
/// edge to edge. A TextBlock measures to its actual text, which is what lets a bubble
/// hug short messages the way a messenger does. Hyperlink and InlineUIContainer (the 3D
/// emoji) work in both, so nothing is lost but caret selection; the bubble's copy
/// action covers that.
/// </para>
/// </summary>
public static class TextBlockHelper
{
    public static readonly DependencyProperty InlineSourceProperty =
        DependencyProperty.RegisterAttached(
            "InlineSource",
            typeof(string),
            typeof(TextBlockHelper),
            new PropertyMetadata(null, OnInlineSourceChanged));

    public static string? GetInlineSource(DependencyObject obj)
        => (string?)obj.GetValue(InlineSourceProperty);

    public static void SetInlineSource(DependencyObject obj, string? value)
        => obj.SetValue(InlineSourceProperty, value);

    private static void OnInlineSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        MessageInlines.Build(tb.Inlines, e.NewValue as string ?? "");
    }
}
