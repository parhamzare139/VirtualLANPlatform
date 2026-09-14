using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

using VirtualLANPlatform.UI.Localization;

namespace VirtualLANPlatform.Core.Chat;

/// <summary>
/// A single chat bubble. Mutable because a message can be deleted in place
/// (the bubble stays, its body turns into a tombstone) — see <see cref="MarkDeleted"/>.
/// </summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    public required string   Id     { get; init; }
    public required string   Sender { get; init; }
    public required DateTime SentAt { get; init; }
    public          bool     IsOwn  { get; init; }

    // ── Reply context (null when the message isn't a reply) ───────────────────
    public string? ReplyToId     { get; init; }
    public string? ReplyToSender { get; init; }
    public string? ReplyToText   { get; init; }

    public bool       HasReply        => ReplyToId is not null;
    public Visibility ReplyVisibility => HasReply ? Visibility.Visible : Visibility.Collapsed;

    private string _text = "";
    public string Text
    {
        get => _text;
        set { _text = value; Notify(); }
    }

    private bool _isDeleted;
    public bool IsDeleted
    {
        get => _isDeleted;
        private set { _isDeleted = value; Notify(); Notify(nameof(CanModify)); Notify(nameof(IsAlive)); }
    }

    /// <summary>Own, still-alive messages are the only ones the user may delete.</summary>
    public bool CanModify => IsOwn && !IsDeleted;
    public bool IsAlive   => !IsDeleted;

    private bool _showHeader = true;
    /// <summary>False for consecutive messages from the same sender — hides avatar and name row.</summary>
    public bool ShowHeader
    {
        get => _showHeader;
        set { _showHeader = value; Notify(); }
    }

    public void MarkDeleted()
    {
        if (IsDeleted) return;
        Text      = Loc.T("Chat_Deleted");
        IsDeleted = true;
    }

    /// <summary>Single-line, clipped preview used when this message is quoted in a reply.</summary>
    public string Preview
    {
        get
        {
            string flat = Text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return flat.Length <= 80 ? flat : flat[..80] + "…";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
