using System.Windows;
using System.Windows.Controls;
using Emoji.Wpf;
using Button      = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace VirtualLANPlatform.UI.Controls;

/// <summary>
/// Full Unicode emoji browser. Glyphs come from Emoji.Wpf, which draws the
/// COLR/CPAL layers of Segoe UI Emoji — i.e. real colour emoji, not the
/// flat monochrome outlines WPF's own text stack falls back to.
/// </summary>
public partial class EmojiPicker : UserControl
{
    private const int PerRow = 9;

    /// <summary>Raised with the chosen emoji, e.g. "🎧".</summary>
    public event Action<string>? Picked;

    private bool _loaded;

    public EmojiPicker()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Builds the tab strip and first page. Called the first time the popup opens
    /// so the emoji database (several thousand entries) never delays startup.
    /// </summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var groups = EmojiData.AllGroups;
            Tabs.ItemsSource = groups;
            if (groups.Count > 0) ShowGroup(groups[0]);
        }
        catch
        {
            GroupLabel.Text = "ایموجی در دسترس نیست";
        }
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EmojiData.Group g }) ShowGroup(g);
    }

    private void ShowGroup(EmojiData.Group group)
    {
        GroupLabel.Text = group.Name;

        var rows    = new List<List<EmojiData.Emoji>>();
        var current = new List<EmojiData.Emoji>(PerRow);

        foreach (var emoji in group.EmojiList)
        {
            if (!emoji.Renderable) continue;

            current.Add(emoji);
            if (current.Count == PerRow)
            {
                rows.Add(current);
                current = new List<EmojiData.Emoji>(PerRow);
            }
        }
        if (current.Count > 0) rows.Add(current);

        Rows.ItemsSource = rows;
        if (rows.Count > 0) Rows.ScrollIntoView(rows[0]);
    }

    private void Emoji_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text } && text.Length > 0)
            Picked?.Invoke(text);
    }
}
