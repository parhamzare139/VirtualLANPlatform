using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VirtualLANPlatform.UI.Emoji;

/// <summary>Loads and caches the bundled 3D emoji PNGs, keyed by the raw emoji character(s).</summary>
public static class Emoji3DImages
{
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    /// <summary>True with a decoded image if <paramref name="emojiText"/> has a bundled 3D asset.</summary>
    public static bool TryGet(string emojiText, out ImageSource? image)
    {
        if (Cache.TryGetValue(emojiText, out image)) return image != null;

        image = null;
        if (Emoji3DCatalog.HexByEmoji.TryGetValue(emojiText, out string? hex))
        {
            try
            {
                var uri = new Uri($"pack://application:,,,/Assets/Emoji3D/{hex}.png");
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption      = BitmapCacheOption.OnLoad;
                // Decode straight to a sane size instead of keeping the full 256px
                // bitmap around and letting the compositor scale it down at draw time.
                bmp.DecodePixelWidth  = 128;
                bmp.DecodePixelHeight = 128;
                bmp.UriSource         = uri;
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
            }
            catch { image = null; }
        }

        Cache[emojiText] = image;
        return image != null;
    }
}
