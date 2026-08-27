using System.Linq;

namespace VirtualLANPlatform.UI.Emoji;

/// <summary>Finds bundled-3D-emoji substrings within plain text, longest match first.</summary>
public static class EmojiTextScanner
{
    public static readonly int MaxKeyLength = Emoji3DCatalog.HexByEmoji.Count == 0
        ? 0
        : Emoji3DCatalog.HexByEmoji.Keys.Max(k => k.Length);

    /// <summary>Attempts to match a known 3D emoji starting exactly at <paramref name="index"/>.</summary>
    public static bool TryMatch(string text, int index, out string emoji)
    {
        int maxLen = Math.Min(MaxKeyLength, text.Length - index);
        for (int len = maxLen; len >= 1; len--)
        {
            string candidate = text.Substring(index, len);
            if (Emoji3DCatalog.HexByEmoji.ContainsKey(candidate))
            {
                emoji = candidate;
                return true;
            }
        }
        emoji = "";
        return false;
    }
}
