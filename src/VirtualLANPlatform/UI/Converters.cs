using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;

namespace VirtualLANPlatform.UI;

/// <summary>Takes the leading character of a name for use as an avatar monogram.</summary>
public sealed class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string s = value as string ?? "";
        s = s.TrimStart();
        return s.Length == 0 ? "؟" : s[..1].ToUpperInvariant();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a plain-text chat message into a WPF FlowDocument.
/// URLs are extracted via regex and rendered as clickable Hyperlinks.
/// Known platforms get a small emoji prefix.
/// </summary>
public sealed class LinkFlowDocumentConverter : IValueConverter
{
    private static readonly Regex UrlRx = new(
        @"https?://[^\s<>""{}|\\^`\[\]]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string text = value as string ?? "";
        var doc  = new FlowDocument();
        var para = new Paragraph { Margin = new Thickness(0), LineHeight = 20 };

        int pos = 0;
        foreach (Match m in UrlRx.Matches(text))
        {
            if (m.Index > pos)
                para.Inlines.Add(new Run(text[pos..m.Index]));

            string url    = m.Value;
            string? emoji = PlatformEmoji(url);
            if (emoji != null)
                para.Inlines.Add(new Run(emoji + " ") { FontSize = 11 });

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
                para.Inlines.Add(link);
            }
            catch { para.Inlines.Add(new Run(url)); }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            para.Inlines.Add(new Run(text[pos..]));

        doc.Blocks.Add(para);
        return doc;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

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
