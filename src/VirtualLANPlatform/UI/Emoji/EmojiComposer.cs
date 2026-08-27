using System.Text;
using System.Windows;
using System.Windows.Documents;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace VirtualLANPlatform.UI.Emoji;

/// <summary>
/// Turns a plain <see cref="RichTextBox"/> into an emoji-aware composer: emoji typed
/// directly or inserted from the picker render as small 3D images instead of falling
/// back to WPF's monochrome glyph outline.
/// </summary>
public static class EmojiComposer
{
    /// <summary>Flattens the document back to a plain string, substituting each inline
    /// emoji image with the original character(s) stored in its Tag.</summary>
    public static string GetPlainText(RichTextBox rtb)
    {
        var sb = new StringBuilder();
        foreach (var block in rtb.Document.Blocks)
            if (block is Paragraph para)
                AppendInlines(sb, para.Inlines);
        return sb.ToString();
    }

    private static void AppendInlines(StringBuilder sb, InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    sb.Append(run.Text);
                    break;
                case InlineUIContainer { Child: FrameworkElement { Tag: string emoji } }:
                    sb.Append(emoji);
                    break;
                case LineBreak:
                    sb.Append('\n');
                    break;
                case Span span:
                    AppendInlines(sb, span.Inlines);
                    break;
            }
        }
    }

    public static void Clear(RichTextBox rtb)
    {
        rtb.Document.Blocks.Clear();
        rtb.Document.Blocks.Add(new Paragraph());
        rtb.CaretPosition = rtb.Document.ContentStart;
    }

    /// <summary>Inserts <paramref name="emoji"/> at the current caret (used by the emoji picker).</summary>
    public static void InsertAtCaret(RichTextBox rtb, string emoji)
    {
        if (Emoji3DImages.TryGet(emoji, out var image) && image != null)
        {
            var img       = EmojiInline.CreateImage(image, emoji, 20);
            var container = new InlineUIContainer(img, rtb.CaretPosition);
            rtb.CaretPosition = container.ElementEnd;
        }
        else
        {
            var caret = rtb.CaretPosition;
            caret.InsertTextInRun(emoji);
            rtb.CaretPosition = caret.GetPositionAtOffset(emoji.Length) ?? caret;
        }
        rtb.Focus();
    }

    /// <summary>
    /// Call after every TextChanged. Converts every known emoji found in the plain text
    /// between the start of the caret's paragraph and the caret — not just whatever sits
    /// immediately before the caret — so a paste containing several emoji (e.g. typed via
    /// the system emoji panel, or "😀 nice 🎉" pasted from elsewhere) converts all of them,
    /// not just the last one. Returns true if at least one swap happened.
    /// </summary>
    public static bool TryConvertNearCaret(RichTextBox rtb)
    {
        bool any = false;
        while (TryConvertOne(rtb)) any = true;
        return any;
    }

    private static bool TryConvertOne(RichTextBox rtb)
    {
        var caret = rtb.CaretPosition;
        var start = caret.Paragraph?.ContentStart;
        if (start == null) return false;

        string text = new TextRange(start, caret).Text;
        if (text.Length == 0) return false;

        for (int i = 0; i < text.Length; i++)
        {
            int maxLen = Math.Min(EmojiTextScanner.MaxKeyLength, text.Length - i);
            for (int len = maxLen; len >= 1; len--)
            {
                string candidate = text.Substring(i, len);
                if (!Emoji3DImages.TryGet(candidate, out var image) || image == null) continue;

                var emojiStart = start.GetPositionAtOffset(i);
                var emojiEnd   = start.GetPositionAtOffset(i + len);
                if (emojiStart == null || emojiEnd == null) continue;

                var range = new TextRange(emojiStart, emojiEnd);
                range.Text = "";
                var img       = EmojiInline.CreateImage(image, candidate, 20);
                var container = new InlineUIContainer(img, range.Start);
                rtb.CaretPosition = container.ElementEnd;
                return true;
            }
        }
        return false;
    }
}
