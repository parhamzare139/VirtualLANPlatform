using System.Windows;
using System.Windows.Media;
using Image = System.Windows.Controls.Image;

namespace VirtualLANPlatform.UI.Emoji;

/// <summary>Builds the inline &lt;Image&gt; used to represent a 3D emoji, both in
/// read-only chat bubbles and in the editable composer.</summary>
public static class EmojiInline
{
    public static Image CreateImage(ImageSource source, string emojiText, double size)
    {
        var img = new Image
        {
            Source = source,
            Width  = size,
            Height = size,
            Margin = new Thickness(1, 0, 1, -3),
            Tag    = emojiText
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }
}
