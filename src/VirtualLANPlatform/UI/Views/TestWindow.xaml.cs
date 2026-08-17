using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MahApps.Metro.IconPacks;
using NAudio.Wave;
// Explicit aliases resolve WinForms vs WPF conflicts
using Brush            = System.Windows.Media.Brush;
using Button           = System.Windows.Controls.Button;
using TextBox          = System.Windows.Controls.TextBox;
using Clipboard        = System.Windows.Clipboard;
using Color            = System.Windows.Media.Color;
using Key              = System.Windows.Input.Key;
using KeyEventArgs     = System.Windows.Input.KeyEventArgs;
using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseWheelEventArgs  = System.Windows.Input.MouseWheelEventArgs;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using VirtualLANPlatform.Core.Chat;
using VirtualLANPlatform.Core.FileTransfer;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;
using VirtualLANPlatform.Core.Room;
using VirtualLANPlatform.Core.ScreenShare;
using VirtualLANPlatform.Core.Services;
using VirtualLANPlatform.Core.Storage;
using VirtualLANPlatform.Core.Voice;

namespace VirtualLANPlatform.UI.Views;

public partial class TestWindow : Window
{
    private readonly DatabaseManager _db   = new();
    private readonly P2PManager      _p2p  = new();
    private readonly RoomManager     _room;
    private readonly ChatManager     _chat;
    private readonly VoiceManager    _voice;
    private readonly FileManager     _file;

    private readonly Dictionary<string, FileNotification> _fileNotifs = new();
    private readonly Dictionary<string, ChatMessage>      _messages   = new();

    /// <summary>Members the host has force-muted, by username.</summary>
    private readonly HashSet<string> _mutedMembers = new(StringComparer.Ordinal);

    /// <summary>Message the composer is currently replying to, if any.</summary>
    private ChatMessage? _replyTarget;
    /// <summary>File notification the composer is currently replying to, if any.</summary>
    private FileNotification? _fileReplyNotif;

    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _busy;
    private CancellationTokenSource? _opCts;
    private CancellationTokenSource? _saveUserCts;
    private readonly System.Windows.Threading.DispatcherTimer _memberTimer;

    private readonly ScreenShareManager _screenShare = new();
    private string?                     _remoteSharerUsername;
    private Storyboard?                 _dotPulse;
    private WaveOutEvent?               _audioOut;
    private BufferedWaveProvider?       _audioBuffer;
    private ScreenShareWindow?          _screenShareWin;

    private ScrollViewer?               _chatScrollViewer;
    private double                      _scrollCurrentOffset;
    private double                      _scrollTargetOffset;
    private System.Windows.Threading.DispatcherTimer? _scrollTimer;
    private TaskCompletionSource<bool>? _modalTcs;

    private static string DataPath(string file) => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VirtualLANPlatform", file);

    private static readonly string UsernamePath = DataPath("username.txt");
    private static readonly string PortPath     = DataPath("port.txt");
    private static readonly string AdapterPath  = DataPath("adapter.txt");
    private static readonly string VolumePath   = DataPath("volume.txt");

    private record AdapterItem(string Name, string IP)
    {
        public override string ToString() => $"{IP}  ({Name})";
    }

    [DllImport("user32.dll")] private static extern bool MessageBeep(uint uType);

    public TestWindow()
    {
        InitializeComponent();
        _room  = new RoomManager(_p2p, _db);
        _chat  = new ChatManager(_p2p);
        _voice = new VoiceManager(_p2p);
        _file  = new FileManager(_p2p);

        _memberTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(1) };
        _memberTimer.Tick += (_, _) => RefreshMemberList();
        _memberTimer.Start();

        InitTrayIcon();
        WireEvents();
        LoadSavedUsername();
        LoadSavedPort();
        LoadSavedVolume();
        LoadAdapters();
        LoadWindowIcon();

        EmojiPickerCtl.Picked += InsertEmoji;
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void LoadWindowIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/logo.png");
            Icon = new System.Windows.Media.Imaging.BitmapImage(uri);
        }
        catch { }
    }

    private static void Save(string path, string value)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, value);
        }
        catch { }
    }

    private static string? Load(string path)
    {
        try { return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private void LoadSavedUsername()
    {
        if (Load(UsernamePath) is { Length: > 0 } saved) UsernameBox.Text = saved;
    }

    private void LoadSavedPort()
    {
        if (Load(PortPath) is { } saved && ushort.TryParse(saved, out ushort p) && p > 0)
            PortBox.Text = saved;
    }

    private void LoadSavedVolume()
    {
        // "mic,speaker" as whole percentages
        var parts = (Load(VolumePath) ?? "").Split(',');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], out double mic) &&
            double.TryParse(parts[1], out double spk))
        {
            MicSlider.Value     = Math.Clamp(mic, 0, 200);
            SpeakerSlider.Value = Math.Clamp(spk, 0, 200);
        }

        _voice.MicGain     = (float)(MicSlider.Value     / 100.0);
        _voice.SpeakerGain = (float)(SpeakerSlider.Value / 100.0);

        // ValueChanged is suppressed until the window is loaded, so label the sliders here.
        MicVolBox.Text = $"{(int)MicSlider.Value}%";
        SpkVolBox.Text = $"{(int)SpeakerSlider.Value}%";
        MicZeroX.Visibility = MicSlider.Value     == 0 ? Visibility.Visible : Visibility.Collapsed;
        SpkZeroX.Visibility = SpeakerSlider.Value == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveVolume()
        => Save(VolumePath, $"{(int)MicSlider.Value},{(int)SpeakerSlider.Value}");

    private void PortBox_LostFocus(object sender, RoutedEventArgs e) => Save(PortPath, GetPort().ToString());

    private void PortBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Save(PortPath, GetPort().ToString());
        e.Handled = true;
    }

    // ── Adapter selection ─────────────────────────────────────────────────────

    private void LoadAdapters()
    {
        AdapterBox.Items.Clear();
        string? savedName = Load(AdapterPath);

        int selectIdx = 0;
        var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                     && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .ToList();

        foreach (var iface in interfaces)
        {
            var ip = iface.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.Address.ToString();
            if (ip == null) continue;

            var item = new AdapterItem(iface.Name, ip);
            int idx = AdapterBox.Items.Add(item);
            if (iface.Name == savedName) selectIdx = idx;
        }

        if (AdapterBox.Items.Count > 0)
            AdapterBox.SelectedIndex = selectIdx;
    }

    private void AdapterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterBox.SelectedItem is AdapterItem item) Save(AdapterPath, item.Name);
    }

    private void RefreshAdapters_Click(object sender, RoutedEventArgs e) => LoadAdapters();

    private string? GetSelectedAdapterIP() => (AdapterBox.SelectedItem as AdapterItem)?.IP;

    // ── Tray icon ─────────────────────────────────────────────────────────────

    private void InitTrayIcon()
    {
        try
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon    = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text    = "Virtual LAN Platform"
            };
        }
        catch { }
    }

    private void ShowNotification(string title, string body)
    {
        try { _trayIcon?.ShowBalloonTip(4000, title, body, System.Windows.Forms.ToolTipIcon.Info); }
        catch { }
    }

    private async void ShowToast(string title, string message, bool isError = false)
    {
        if (ToastPanel.Children.OfType<Border>().Any(b => b.Tag is string t && t == title))
            return;

        var panel = new StackPanel { FlowDirection = System.Windows.FlowDirection.RightToLeft };
        panel.Children.Add(new TextBlock
        {
            Text       = title,
            Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize   = 12.5
        });
        if (message.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text         = message,
                Foreground   = Res("TxtMid"),
                FontSize     = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 3, 0, 0)
            });

        var toast = new Border
        {
            Tag             = title,
            Background      = new System.Windows.Media.SolidColorBrush(
                                  System.Windows.Media.Color.FromRgb(0x1E, 0x24, 0x33)),
            CornerRadius    = new CornerRadius(10),
            BorderBrush     = new System.Windows.Media.SolidColorBrush(isError
                                  ? System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44)
                                  : System.Windows.Media.Color.FromRgb(0x63, 0x66, 0xF1)),
            BorderThickness = new Thickness(0, 0, 3, 0),
            Padding         = new Thickness(14, 10, 14, 10),
            Margin          = new Thickness(0, 0, 0, 6),
            Opacity         = 0,
            Child           = panel,
            Effect          = new System.Windows.Media.Effects.DropShadowEffect
                              { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45 }
        };

        ToastPanel.Children.Insert(0, toast);

        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(0.18)));
        toast.BeginAnimation(OpacityProperty, fadeIn);

        await Task.Delay(4000);

        if (!ToastPanel.Children.Contains(toast)) return;
        var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromSeconds(0.3)));
        toast.BeginAnimation(OpacityProperty, fadeOut);
        await Task.Delay(320);
        ToastPanel.Children.Remove(toast);
    }

    private Task<bool> ShowModal(string title, string message,
        string confirmText = "تأیید", string? cancelText = "لغو", bool isDanger = false)
    {
        ModalTitle.Text   = title;
        ModalMessage.Text = message;
        ModalButtons.Children.Clear();

        _modalTcs?.TrySetResult(false);
        _modalTcs = new TaskCompletionSource<bool>();

        var confirmBtn = new Button
        {
            Content = confirmText,
            Style   = (Style)(isDanger ? FindResource("Btn.Danger") : FindResource("Btn.Primary")),
            Padding = new Thickness(20, 9, 20, 9),
            Margin  = new Thickness(0, 0, cancelText != null ? 8 : 0, 0),
            Cursor  = System.Windows.Input.Cursors.Hand
        };
        confirmBtn.Click += (_, _) => { ModalOverlay.Visibility = Visibility.Collapsed; _modalTcs.TrySetResult(true); };
        ModalButtons.Children.Add(confirmBtn);

        if (cancelText != null)
        {
            var cancelBtn = new Button
            {
                Content = cancelText,
                Style   = (Style)FindResource("Btn.Ghost"),
                Padding = new Thickness(20, 9, 20, 9),
                Cursor  = System.Windows.Input.Cursors.Hand
            };
            cancelBtn.Click += (_, _) => { ModalOverlay.Visibility = Visibility.Collapsed; _modalTcs.TrySetResult(false); };
            ModalButtons.Children.Add(cancelBtn);
        }

        ModalOverlay.Visibility = Visibility.Visible;
        return _modalTcs.Task;
    }

    private void ModalOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.Source != ModalOverlay) return;
        ModalOverlay.Visibility = Visibility.Collapsed;
        _modalTcs?.TrySetResult(false);
    }

    private void ModalDialog_StopPropagation(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void PlayDownloadSound()
    {
        try
        {
            string[] candidates = [
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    @"Media\Windows Notify Messaging.wav"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    @"Media\Windows Ding.wav"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    @"Media\chord.wav"),
            ];
            string? wav = candidates.FirstOrDefault(System.IO.File.Exists);
            if (wav != null)
            {
                var mp = new System.Windows.Media.MediaPlayer();
                mp.Open(new Uri(wav, UriKind.Absolute));
                mp.Play();
            }
            else MessageBeep(0x00000040);
        }
        catch { }
    }

    // ── Room navigation ───────────────────────────────────────────────────────

    private void EnterRoom(string ipPort, bool isHost)
    {
        LobbyActions.Visibility     = Visibility.Collapsed;
        LobbyHeaderPanel.Visibility = Visibility.Collapsed;
        Footer.Visibility           = Visibility.Collapsed;

        RoomHeaderPanel.Opacity         = 0;
        RoomHeaderPanel.RenderTransform = new TranslateTransform(18, 0);
        RoomHeaderPanel.Visibility      = Visibility.Visible;

        var enterSb = new Storyboard();
        void Add(DependencyObject t, PropertyPath p, double from, double to, double secs, IEasingFunction? ease = null)
        {
            var a = new DoubleAnimation(from, to, new Duration(TimeSpan.FromSeconds(secs))) { EasingFunction = ease };
            Storyboard.SetTarget(a, t); Storyboard.SetTargetProperty(a, p);
            enterSb.Children.Add(a);
        }
        Add(RoomHeaderPanel, new PropertyPath("Opacity"), 0, 1, 0.3);
        Add(RoomHeaderPanel, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"), 18, 0, 0.3,
            new CubicEase { EasingMode = EasingMode.EaseOut });
        enterSb.Begin();

        LeaveCloseText.Text     = isHost ? "بستن Room" : "خروج از Room";
        LeaveCloseBtn.IsEnabled = true;

        HostIpLabel.Text       = isHost ? ipPort : "";
        HostIpPanel.Visibility = isHost ? Visibility.Visible : Visibility.Collapsed;

        // The profile column is lobby-only; giving its width to the chat in a
        // room is worth more than an idle username box.
        ProfileSidebar.Visibility = Visibility.Collapsed;
        VoiceStrip.Visibility     = Visibility.Visible;
        ChatPanel.Visibility      = Visibility.Visible;
        RightSidebar.Visibility   = Visibility.Visible;

        MicSlider.IsEnabled     = true;
        SpeakerSlider.IsEnabled = true;

        ChatInput.Focus();
        SetStatus(isHost ? "Host — منتظر اتصال" : "متصل", StatusKind.Ok);
    }

    private void LeaveRoom()
    {
        LobbyActions.Visibility     = Visibility.Visible;
        LobbyHeaderPanel.Visibility = Visibility.Visible;
        RoomHeaderPanel.Visibility  = Visibility.Collapsed;
        Footer.Visibility           = Visibility.Visible;

        ProfileSidebar.Visibility = Visibility.Visible;
        VoiceStrip.Visibility     = Visibility.Collapsed;
        ChatPanel.Visibility      = Visibility.Collapsed;
        RightSidebar.Visibility   = Visibility.Collapsed;

        LeaveCloseBtn.IsEnabled = false;
        SetRoomControlsEnabled(false);
        MicSlider.IsEnabled     = false;
        SpeakerSlider.IsEnabled = false;

        if (_screenShare.IsSharing)
        {
            _screenShare.Stop();
            _room.BroadcastScreenShareStop();
            SetShareButton(sharing: false);
        }
        StopAudioPlayback();
        _remoteSharerUsername = null;
        _screenShareWin?.Close();

        SetMicVisual(true);
        SetSpeakerVisual(true);

        MemberList.Items.Clear();
        ChatList.Items.Clear();
        _fileNotifs.Clear();
        _messages.Clear();
        _mutedMembers.Clear();
        ClearReply();
        VoiceStatus.Text = "—";

        ResetDebug();
        SetStatus("آماده", StatusKind.Idle);
    }

    private void SetRoomControlsEnabled(bool on)
    {
        MicBtn.IsEnabled         = on;
        SpeakerBtn.IsEnabled     = on;
        SendFileBtn.IsEnabled    = on;
        ScreenShareBtn.IsEnabled = on && _remoteSharerUsername == null;
    }

    // ── Event wiring ──────────────────────────────────────────────────────────

    private void WireEvents()
    {
        _room.StatusChanged += msg => Dispatch(() => StatusLabel.Text = msg);
        _p2p.StatusChanged  += msg => Dispatch(() => StatusLabel.Text = msg);

        _p2p.PeerConnected += info => Dispatch(() =>
        {
            DbgPeers.Text = _p2p.PeerCount.ToString();
            SetStatus("متصل", StatusKind.Ok);
            SetRoomControlsEnabled(true);
            RefreshMemberList();
        });

        _p2p.EncryptionEstablished += (peerId, ok) => Dispatch(() =>
        {
            DbgEncryption.Text       = ok ? "AES-256-GCM" : "خطای کلید";
            DbgEncIcon.Kind          = ok ? PackIconLucideKind.ShieldCheck : PackIconLucideKind.ShieldAlert;
            var brush                = ok ? Res("Ok") : Res("Danger");
            DbgEncryption.Foreground = brush;
            DbgEncIcon.Foreground    = brush;
        });

        _p2p.PeerDisconnected += (id, reason) => Dispatch(() =>
        {
            DbgPeers.Text = _p2p.PeerCount.ToString();
            if (_p2p.PeerCount == 0 && _room.IsActive)
            {
                SetStatus("قطع شده", StatusKind.Idle);
                SetRoomControlsEnabled(false);
            }
            RefreshMemberList();
        });

        _p2p.ConnectionFailed += (title, msg) => Dispatch(() =>
        {
            SetStatus("خطا", StatusKind.Danger);
            SetBusy(false);
            ShowToast(title, msg, isError: true);
        });

        _p2p.ConnectionRejected += reason => Dispatch(() =>
        {
            SetBusy(false);
            ShowToast("اتصال رد شد", reason, isError: true);
        });

        _p2p.MessageReceived += (id, frame) => Dispatch(RefreshMemberList);

        _room.MemberJoined += _ => Dispatch(RefreshMemberList);
        _room.MemberLeft   += _ => Dispatch(RefreshMemberList);

        _room.RoomClosed += msg => Dispatch(() =>
        {
            _room.Shutdown();
            _voice.Reset();
            LeaveRoom();
            Announce(msg, "Room بسته شد", MessageBoxImage.Information);
        });

        _room.ModerationReceived += (op, on) => Dispatch(() => ApplyModeration(op, on));

        // ── Screen Share ──────────────────────────────────────────────────────
        _screenShare.FrameCaptured += bytes =>
        {
            _room.BroadcastScreenShareFrame(bytes);
            Dispatch(() => _screenShareWin?.UpdateFrame(Decode(bytes)));
        };

        _screenShare.AudioCaptured += SendAudio;

        _room.ScreenShareAudioReceived += packet => Dispatch(() => PlayAudio(packet));

        _room.ScreenShareStarted += username => Dispatch(() =>
        {
            _remoteSharerUsername    = username;
            ScreenShareBtn.IsEnabled = false;
            OpenScreenShareWindow(username);
        });

        _room.ScreenShareFrame += (_, bytes) => Dispatch(() => _screenShareWin?.UpdateFrame(Decode(bytes)));

        _room.ScreenShareStopped += _ => Dispatch(() =>
        {
            _remoteSharerUsername = null;
            _screenShareWin?.SetStopped();
            StopAudioPlayback();
            if (_p2p.PeerCount > 0) ScreenShareBtn.IsEnabled = true;
        });

        // ── Chat ──────────────────────────────────────────────────────────────
        _chat.MessageReceived += msg => Dispatch(() => AddChatMessage(msg));
        _chat.MessageDeleted  += id  => Dispatch(() =>
        {
            if (_messages.TryGetValue(id, out var msg))
            {
                ChatList.Items.Remove(msg);
                _messages.Remove(id);
            }
            if (_replyTarget?.Id == id) ClearReply();
        });

        // ── Voice ─────────────────────────────────────────────────────────────
        _voice.MicChanged     += active => Dispatch(() => SetMicVisual(active));
        _voice.SpeakerChanged += active => Dispatch(() => SetSpeakerVisual(active));
        _voice.StatusChanged  += msg    => Dispatch(() => VoiceStatus.Text = msg);

        // ── File transfer ──────────────────────────────────────────────────────
        _file.IncomingFile += info => Dispatch(() =>
        {
            var notif = new FileNotification
            {
                Id          = info.Id,
                FileName    = info.FileName,
                SizeText    = FormatSize(info.Size),
                IsSent      = false,
                Status      = "آماده دانلود",
                CanDownload = true
            };
            _fileNotifs[info.Id] = notif;
            ChatList.Items.Add(notif);
            ChatList.ScrollIntoView(notif);
            Activate();
            VoiceStatus.Text = $"فایل دریافتی: {info.FileName}";
        });

        _file.TransferAccepted += id => Dispatch(() =>
        {
            if (_fileNotifs.TryGetValue(id, out var notif) && notif.IsSent)
                notif.Status = "در حال ارسال...";
        });

        _file.TransferProgress += (id, done, total) => Dispatch(() =>
        {
            int pct = total > 0 ? (int)(done * 100.0 / total) : 100;
            if (_fileNotifs.TryGetValue(id, out var notif))
                notif.Status = notif.IsSent
                    ? $"ارسال {pct}%  ({done}/{total})"
                    : $"دانلود {pct}%  ({done}/{total})";
            VoiceStatus.Text = $"انتقال فایل: {pct}%";
        });

        _file.TransferComplete += (id, path) => Dispatch(() =>
        {
            string name = System.IO.Path.GetFileName(path);
            if (_fileNotifs.TryGetValue(id, out var notif))
            {
                bool sent = notif.IsSent;
                notif.Status      = sent ? "✓ ارسال شد" : "✓ ذخیره شد";
                notif.CanDownload = false;
                _fileNotifs.Remove(id);
                VoiceStatus.Text = sent ? $"ارسال شد: {name}" : $"دانلود شد: {name}";

                if (!sent)
                {
                    PlayDownloadSound();
                    ShowNotification("دانلود کامل شد", $"✓ {name}  →  Downloads");
                }
            }
            else VoiceStatus.Text = $"کامل شد: {name}";
        });

        _file.TransferFailed += (id, reason) => Dispatch(() =>
        {
            if (_fileNotifs.TryGetValue(id, out var notif))
            {
                notif.Status      = notif.IsSent ? $"✗ ارسال ناموفق: {reason}" : $"✗ خطا: {reason}";
                notif.CanDownload = false;
                _fileNotifs.Remove(id);
            }
            VoiceStatus.Text = $"خطا: {reason}";
        });

        _file.StatusChanged += msg => Dispatch(() => VoiceStatus.Text = msg);
    }

    private static string FormatSize(long size) => size < 1024 * 1024
        ? $"{size / 1024.0:F1} KB"
        : $"{size / (1024.0 * 1024):F1} MB";

    private static System.Windows.Media.Imaging.BitmapImage Decode(byte[] jpeg)
    {
        using var ms = new System.IO.MemoryStream(jpeg);
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // ── Room lifecycle handlers ───────────────────────────────────────────────

    private async void CreateRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        SetStatus("در حال ایجاد Room...", StatusKind.Warn);

        string username = UsernameBox.Text.Trim() is { Length: > 0 } u ? u : "Host";
        Save(UsernamePath, username);
        _chat.SetUsername(username);

        try
        {
            var (ok, localIp, _) = await _room.CreateRoomAsync(
                username, port: GetPort(), localIp: GetSelectedAdapterIP(), ct: _opCts!.Token);

            if (ok)
            {
                DbgRole.Text = "Host";
                RefreshMemberList();
                EnterRoom(localIp, isHost: true);
            }
            else SetStatus("خطا در ایجاد Room", StatusKind.Danger);
        }
        catch (OperationCanceledException)
        {
            _room.Shutdown();
            SetStatus("لغو شد", StatusKind.Idle);
        }
        finally { SetBusy(false); }
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string hostIp = JoinCodeBox.Text.Trim();
        if (hostIp.Length == 0)
        {
            ShowToast("خطا", "لطفاً IP هاست را وارد کنید", isError: true);
            return;
        }

        SetBusy(true);
        SetStatus("در حال اتصال...", StatusKind.Warn);

        string username = UsernameBox.Text.Trim() is { Length: > 0 } u ? u : "Guest";
        Save(UsernamePath, username);
        _chat.SetUsername(username);

        try
        {
            bool ok = await _room.JoinRoomAsync(hostIp, GetPort(), username, _opCts!.Token);
            if (ok)
            {
                DbgRole.Text = "Guest";
                RefreshMemberList();
                EnterRoom(hostIp, isHost: false);
            }
            else
            {
                SetStatus("اتصال ناموفق", StatusKind.Danger);
                ShowToast("اتصال ناموفق", "اتصال برقرار نشد — IP و فایروال را بررسی کنید", isError: true);
            }
        }
        catch (OperationCanceledException)
        {
            _room.Shutdown();
            SetStatus("لغو شد", StatusKind.Idle);
        }
        finally { SetBusy(false); }
    }

    private async void SaveUsername_Click(object sender, RoutedEventArgs e)
    {
        string username = UsernameBox.Text.Trim();
        if (username.Length == 0) return;
        Save(UsernamePath, username);

        _saveUserCts?.Cancel();
        _saveUserCts = new CancellationTokenSource();
        var cts = _saveUserCts;

        SaveText.Text = "ذخیره شد";
        SaveIcon.Kind = PackIconLucideKind.Check;
        SaveUsernameBtn.Foreground = Res("Ok");

        try
        {
            await Task.Delay(1800, cts.Token);
            SaveText.Text = "ذخیره";
            SaveIcon.Kind = PackIconLucideKind.Save;
            SaveUsernameBtn.ClearValue(ForegroundProperty);
        }
        catch (OperationCanceledException) { }
    }

    private async void HostIpBorder_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            Clipboard.SetText(HostIpLabel.Text);
            IpCopiedLabel.Opacity = 1;
            await Task.Delay(1800);
            IpCopiedLabel.Opacity = 0;
        }
        catch { }
    }

    private void NavDonate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "https://donate.virtuallan.ir",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void LeaveClose_Click(object sender, RoutedEventArgs e)
    {
        bool isHost = _room.IsHost;
        bool confirmed = await ShowModal(
            isHost ? "بستن Room" : "خروج از Room",
            isHost ? "Room برای همه اعضا بسته می‌شود. مطمئن هستید؟"
                   : "از Room خارج می‌شوید. مطمئن هستید؟",
            isHost ? "بستن Room" : "خروج",
            "لغو", isDanger: true);
        if (!confirmed) return;

        LeaveCloseBtn.IsEnabled = false;
        if (isHost) await _room.CloseRoomAsync();
        else        await _room.LeaveRoomAsync();
        _voice.Reset();
        LeaveRoom();
    }

    private void CancelOp_Click(object sender, RoutedEventArgs e) => _opCts?.Cancel();

    // ── Voice ─────────────────────────────────────────────────────────────────

    private void Mic_Click(object sender, RoutedEventArgs e)     => _voice.ToggleMic();
    private void Speaker_Click(object sender, RoutedEventArgs e) => _voice.ToggleSpeaker();

    private void SetMicVisual(bool active)
    {
        MicIcon.Kind    = active ? PackIconLucideKind.Mic : PackIconLucideKind.MicOff;
        MicBtn.Background = active ? Res("OkGrad") : Res("DangerGrad");
        MicBtn.ToolTip  = _voice.IsForceMuted
            ? "میکروفون توسط میزبان بسته شده"
            : active ? "میکروفون روشن" : "میکروفون خاموش";
    }

    private void SetSpeakerVisual(bool active)
    {
        SpeakerIcon.Kind      = active ? PackIconLucideKind.Volume2 : PackIconLucideKind.VolumeOff;
        SpeakerBtn.Background = active ? Res("OkGrad") : Res("DangerGrad");
        SpeakerBtn.ToolTip    = active ? "اسپیکر روشن" : "اسپیکر خاموش";
    }

    private void MicSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _voice.MicGain      = (float)(e.NewValue / 100.0);
        MicVolBox.Text      = $"{(int)e.NewValue}%";
        MicZeroX.Visibility = e.NewValue == 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveVolume();
    }

    private void SpeakerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _voice.SpeakerGain  = (float)(e.NewValue / 100.0);
        SpkVolBox.Text      = $"{(int)e.NewValue}%";
        SpkZeroX.Visibility = e.NewValue == 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveVolume();
    }

    private void MicVolBox_LostFocus(object sender, RoutedEventArgs e) => ApplyVolBox(MicVolBox, MicSlider);
    private void SpkVolBox_LostFocus(object sender, RoutedEventArgs e) => ApplyVolBox(SpkVolBox, SpeakerSlider);

    private void VolBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender == MicVolBox) ApplyVolBox(MicVolBox, MicSlider);
        if (sender == SpkVolBox) ApplyVolBox(SpkVolBox, SpeakerSlider);
        e.Handled = true;
    }

    private void ApplyVolBox(TextBox box, Slider slider)
    {
        string raw = box.Text.TrimEnd('%').Trim();
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double v))
        {
            double clamped = Math.Clamp(v, 0, 200);
            slider.Value = clamped;
            box.Text     = $"{(int)clamped}%";
        }
        else
            box.Text = $"{(int)slider.Value}%";
    }

    // ── Moderation ────────────────────────────────────────────────────────────

    /// <summary>Runs on a guest when the host sends a command against it.</summary>
    private void ApplyModeration(string op, bool on)
    {
        switch (op)
        {
            case "mute":
                _voice.SetForceMuted(on);
                SetMicVisual(_voice.IsMicActive);
                break;

            case "stopshare":
                if (_screenShare.IsSharing)
                {
                    _screenShare.Stop();
                    _room.BroadcastScreenShareStop();
                    SetShareButton(sharing: false);
                    _screenShareWin?.Close();
                    VoiceStatus.Text = "میزبان اشتراک صفحه شما را قطع کرد";
                }
                break;

            case "kick":
                _room.Shutdown();
                _voice.Reset();
                LeaveRoom();
                Announce("شما توسط میزبان از Room اخراج شدید.", "اخراج", MessageBoxImage.Warning);
                break;
        }
    }

    private void ModMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemberItem m }) return;

        bool mute = !m.IsMuted;
        if (!_room.MuteMember(m.Username, mute)) return;

        if (mute) _mutedMembers.Add(m.Username);
        else      _mutedMembers.Remove(m.Username);

        m.IsMuted        = mute;
        VoiceStatus.Text = mute ? $"{m.Username} میوت شد" : $"{m.Username} از میوت خارج شد";
    }

    private void ModStopShare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemberItem m }) return;
        if (_room.StopMemberShare(m.Username))
            VoiceStatus.Text = $"درخواست قطع اشتراک صفحه برای {m.Username} ارسال شد";
    }

    private async void ModKick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemberItem m }) return;

        bool confirmed = await ShowModal("اخراج کاربر", $"«{m.Username}» از Room اخراج شود؟",
            "اخراج", "لغو", isDanger: true);
        if (!confirmed) return;

        if (_room.KickMember(m.Username))
        {
            _mutedMembers.Remove(m.Username);
            VoiceStatus.Text = $"{m.Username} اخراج شد";
        }
    }

    // ── File transfer ─────────────────────────────────────────────────────────

    private void SendFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "انتخاب فایل برای ارسال" };
        if (dlg.ShowDialog() != true) return;

        string path = dlg.FileName;
        string name = System.IO.Path.GetFileName(path);
        long   size = new System.IO.FileInfo(path).Length;

        if (size == 0)
        {
            ShowToast("فایل خالی", $"«{name}» قابل ارسال نیست (0 بایت)", isError: true);
            return;
        }

        string transferId = Guid.NewGuid().ToString("N")[..12];
        var notif = new FileNotification
        {
            Id = transferId, FileName = name, SizeText = FormatSize(size),
            IsSent = true, Status = "در انتظار پذیرش...", CanDownload = false
        };
        _fileNotifs[transferId] = notif;
        ChatList.Items.Add(notif);
        ChatList.ScrollIntoView(notif);

        _ = Task.Run(() => _file.SendFileAsync(path, transferId));
    }

    private void DownloadFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileNotification notif }) return;
        notif.CanDownload = false;
        notif.Status      = "در حال دانلود...";
        _file.AcceptTransfer(notif.Id, _file.GetSavePath(notif.FileName));
    }

    // ── Chat ──────────────────────────────────────────────────────────────────

    private void SendChat_Click(object sender, RoutedEventArgs e) => DoSendChat();

    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_replyTarget != null || _fileReplyNotif != null))
        {
            ClearReply();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && (e.KeyboardDevice.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
        {
            DoSendChat();
            e.Handled = true;
        }
    }

    private void DoSendChat()
    {
        string text = ChatInput.Text.Trim();
        if (text.Length == 0) return;

        if (!_p2p.IsRunning)
        {
            ShowToast("خطا", "ابتدا به Room متصل شوید.", isError: true);
            return;
        }

        if (_fileReplyNotif != null)
        {
            var stub = new ChatMessage
            {
                Id     = "file:" + _fileReplyNotif.Id,
                Sender = "📎 فایل",
                Text   = _fileReplyNotif.FileName,
                SentAt = DateTime.Now
            };
            _chat.SendToAll(text, stub);
        }
        else
        {
            _chat.SendToAll(text, _replyTarget);
        }
        ChatInput.Clear();
        ClearReply();
    }

    private void AddChatMessage(ChatMessage msg)
    {
        if (ChatList.Items.Count > 0 &&
            ChatList.Items[^1] is ChatMessage prev &&
            prev.Sender == msg.Sender && !prev.IsDeleted &&
            (msg.SentAt - prev.SentAt).TotalMinutes < 3 &&
            msg.ReplyToId == null)
        {
            msg.ShowHeader = false;
        }

        _messages[msg.Id] = msg;
        ChatList.Items.Add(msg);
        ChatList.ScrollIntoView(msg);
    }

    private void ReplyTo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatMessage msg }) return;

        _replyTarget       = msg;
        ReplyToName.Text   = $"پاسخ به {msg.Sender}";
        ReplyToText.Text   = msg.Preview;
        ReplyBar.Visibility = Visibility.Visible;
        ChatInput.Focus();
    }

    private void CancelReply_Click(object sender, RoutedEventArgs e) => ClearReply();

    private void ClearReply()
    {
        _replyTarget        = null;
        _fileReplyNotif     = null;
        ReplyBar.Visibility = Visibility.Collapsed;
    }

    private void ReplyToFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileNotification notif }) return;
        _replyTarget        = null;
        _fileReplyNotif     = notif;
        ReplyToName.Text    = "پاسخ به فایل";
        ReplyToText.Text    = notif.FileName;
        ReplyBar.Visibility = Visibility.Visible;
        ChatInput.Focus();
    }

    private void ReplyQuote_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        if (!_messages.TryGetValue(id, out var msg)) return;
        ChatList.ScrollIntoView(msg);
    }

    private async void DeleteMsg_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatMessage msg }) return;
        if (!msg.CanModify) return;

        bool confirmed = await ShowModal("حذف پیام", "این پیام برای همه حذف شود؟",
            "حذف", "لغو", isDanger: true);
        if (!confirmed) return;

        _chat.DeleteMessage(msg.Id);
    }

    private void ChatList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        SmoothScrollChat(e.Delta);
    }

    private void SmoothScrollChat(int wheelDelta)
    {
        var sv = GetChatScrollViewer();
        if (sv == null) return;

        if (_scrollTimer == null)
        {
            _scrollTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(16) };
            _scrollTimer.Tick += ScrollAnimationTick;
        }

        if (!_scrollTimer.IsEnabled)
        {
            _scrollCurrentOffset = sv.VerticalOffset;
            _scrollTargetOffset  = sv.VerticalOffset;
        }

        _scrollTargetOffset = Math.Clamp(
            _scrollTargetOffset - wheelDelta / 2.0, 0, sv.ScrollableHeight);

        _scrollTimer.Start();
    }

    private void ScrollAnimationTick(object? sender, EventArgs e)
    {
        var sv = GetChatScrollViewer();
        if (sv == null) { _scrollTimer!.Stop(); return; }

        _scrollCurrentOffset += (_scrollTargetOffset - _scrollCurrentOffset) * 0.25;
        sv.ScrollToVerticalOffset(_scrollCurrentOffset);

        if (Math.Abs(_scrollTargetOffset - _scrollCurrentOffset) < 0.5)
        {
            sv.ScrollToVerticalOffset(_scrollTargetOffset);
            _scrollCurrentOffset = _scrollTargetOffset;
            _scrollTimer!.Stop();
        }
    }

    private ScrollViewer? GetChatScrollViewer()
    {
        if (_chatScrollViewer != null) return _chatScrollViewer;
        _chatScrollViewer = FindDescendant<ScrollViewer>(ChatList);
        return _chatScrollViewer;
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (e.OriginalSource is not DependencyObject src) return;
        if (System.Windows.Input.Keyboard.FocusedElement is not TextBox focused) return;
        if (IsChildOf(src, focused)) return;

        // Volume boxes: apply value directly before focus moves
        if (focused == MicVolBox) ApplyVolBox(MicVolBox, MicSlider);
        else if (focused == SpkVolBox) ApplyVolBox(SpkVolBox, SpeakerSlider);

        // Clear focus → fires LostFocus on all TextBoxes (PortBox saves, etc.)
        System.Windows.Input.Keyboard.ClearFocus();
    }

    private static bool IsChildOf(DependencyObject element, DependencyObject parent)
    {
        var current = element;
        while (current != null)
        {
            if (current == parent) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    // ── Emoji ─────────────────────────────────────────────────────────────────

    private DateTime _emojiPopupClosedAt = DateTime.MinValue;

    private void EmojiPopup_Closed(object? sender, EventArgs e)
        => _emojiPopupClosedAt = DateTime.UtcNow;

    private void EmojiBtn_Click(object sender, RoutedEventArgs e)
    {
        // StaysOpen=False already dismissed the popup on the mouse-down that
        // preceded this click; without the guard the click would reopen it.
        if ((DateTime.UtcNow - _emojiPopupClosedAt).TotalMilliseconds < 250) return;

        EmojiPickerCtl.EnsureLoaded();
        EmojiPopup.IsOpen = !EmojiPopup.IsOpen;
    }

    private void InsertEmoji(string emoji)
    {
        string text = ChatInput.Text;

        // Emoji.Wpf.TextBox delegates editing to a RichTextBox inside its template,
        // so the shell's caret index isn't always live. Treat 0 as "no caret info"
        // and append, which is what a composer should do anyway.
        int caret = ChatInput.SelectionStart;
        if (caret <= 0 || caret > text.Length) caret = text.Length;

        ChatInput.Text = text.Insert(caret, emoji);
        ChatInput.SelectionStart  = caret + emoji.Length;
        ChatInput.SelectionLength = 0;
        ChatInput.Focus();
    }

    // ── Screen share ──────────────────────────────────────────────────────────

    private void OpenScreenShareWindow(string sharerName)
    {
        _screenShareWin?.Close();
        _screenShareWin = new ScreenShareWindow(sharerName);
        _screenShareWin.Closed += (_, _) => _screenShareWin = null;
        _screenShareWin.Show();
    }

    private void SetShareButton(bool sharing)
    {
        ScreenShareIcon.Kind = sharing ? PackIconLucideKind.MonitorStop : PackIconLucideKind.MonitorUp;
        ScreenShareText.Text = sharing ? "توقف اشتراک" : "اشتراک صفحه";
        ScreenShareBtn.Foreground = sharing ? Res("Danger") : Res("TxtMid");
    }

    private void ScreenShare_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteSharerUsername != null) return;

        if (_screenShare.IsSharing)
        {
            _screenShare.Stop();
            _room.BroadcastScreenShareStop();
            StopAudioPlayback();
            SetShareButton(sharing: false);
            _screenShareWin?.Close();
        }
        else
        {
            var picker = new WindowPickerDialog { Owner = this };
            if (picker.ShowDialog() != true) return;

            _room.BroadcastScreenShareStart();
            _screenShare.Start(fps: 8,
                windowHandle: picker.SelectedHandle,
                shareAudio:   picker.ShareAudio);

            SetShareButton(sharing: true);
            OpenScreenShareWindow("من");
        }
    }

    // ── Audio (screen-share) ──────────────────────────────────────────────────

    private void SendAudio(byte[] pcm, WaveFormat fmt)
    {
        var packet = new byte[10 + pcm.Length];
        BitConverter.GetBytes(fmt.SampleRate).CopyTo(packet, 0);
        BitConverter.GetBytes((short)fmt.Channels).CopyTo(packet, 4);
        BitConverter.GetBytes((short)fmt.BitsPerSample).CopyTo(packet, 6);
        BitConverter.GetBytes((short)(int)fmt.Encoding).CopyTo(packet, 8);
        pcm.CopyTo(packet, 10);
        _room.BroadcastScreenShareAudio(packet);
    }

    private void PlayAudio(byte[] packet)
    {
        if (packet.Length < 10) return;
        int sampleRate    = BitConverter.ToInt32(packet, 0);
        int channels      = BitConverter.ToInt16(packet, 4);
        int bitsPerSample = BitConverter.ToInt16(packet, 6);
        int encoding      = BitConverter.ToInt16(packet, 8);

        var fmt = encoding == 3
            ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
            : new WaveFormat(sampleRate, bitsPerSample, channels);

        if (_audioBuffer == null || _audioBuffer.WaveFormat.SampleRate != sampleRate
            || _audioBuffer.WaveFormat.Channels != channels)
        {
            StopAudioPlayback();
            _audioBuffer = new BufferedWaveProvider(fmt) { DiscardOnBufferOverflow = true };
            _audioOut    = new WaveOutEvent();
            _audioOut.Init(_audioBuffer);
            _audioOut.Play();
        }

        _audioBuffer.AddSamples(packet, 10, packet.Length - 10);
    }

    private void StopAudioPlayback()
    {
        try { _audioOut?.Stop(); } catch { }
        _audioOut?.Dispose();
        _audioOut    = null;
        _audioBuffer = null;
    }

    // ── Members ───────────────────────────────────────────────────────────────

    private void RefreshMemberList()
    {
        var members = _room.GetMembers().OrderBy(m => m.Username, StringComparer.Ordinal).ToList();

        // Skip the rebuild when nothing changed — this runs on a 1 s timer.
        if (MemberList.Items.Count == members.Count &&
            members.Select((m, i) => (MemberList.Items[i] as MemberItem)?.Username == m.Username).All(x => x))
        {
            MemberCount.Text = members.Count.ToString();
            return;
        }

        MemberList.Items.Clear();
        foreach (var m in members)
        {
            bool isSelf = m.Username == _room.MyUsername;
            MemberList.Items.Add(new MemberItem
            {
                Username    = m.Username,
                IsSelf      = isSelf,
                // Only the host can tell who the host is — a guest's member list
                // arrives over the handshake with no peer ids attached.
                IsHostRole  = _room.IsHost && m.PeerId == -1,
                CanModerate = _room.IsHost && !isSelf && m.PeerId >= 0,
                IsMuted     = _mutedMembers.Contains(m.Username)
            });
        }

        MemberCount.Text = members.Count.ToString();
        DbgPeers.Text    = (members.Count - (_room.IsHost ? 1 : 0)).ToString();
    }

    private void ResetDebug()
    {
        DbgRole.Text             = "—";
        DbgPeers.Text            = "0";
        MemberCount.Text         = "0";
        DbgEncryption.Text       = "بدون رمز";
        DbgEncIcon.Kind          = PackIconLucideKind.ShieldOff;
        DbgEncryption.Foreground = Res("Warn");
        DbgEncIcon.Foreground    = Res("Warn");
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private enum StatusKind { Idle, Ok, Warn, Danger }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusLabel.Text = text;
        StatusDot.Color = kind switch
        {
            StatusKind.Ok     => Color.FromRgb(0x22, 0xC5, 0x5E),
            StatusKind.Warn   => Color.FromRgb(0xF5, 0x9E, 0x0B),
            StatusKind.Danger => Color.FromRgb(0xEF, 0x44, 0x44),
            _                 => Color.FromRgb(0x5B, 0x63, 0x77)
        };

        _dotPulse?.Stop();
        if (kind != StatusKind.Ok) return;

        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var anim = new DoubleAnimation(1.0, 1.35, new Duration(TimeSpan.FromSeconds(0.75)))
        {
            AutoReverse    = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(anim, StatusEllipse);
        Storyboard.SetTargetProperty(anim, new PropertyPath("RenderTransform.ScaleX"));
        var anim2 = anim.Clone();
        Storyboard.SetTarget(anim2, StatusEllipse);
        Storyboard.SetTargetProperty(anim2, new PropertyPath("RenderTransform.ScaleY"));
        sb.Children.Add(anim);
        sb.Children.Add(anim2);
        _dotPulse = sb;
        sb.Begin();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Brush Res(string key) => (Brush)FindResource(key);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CreateBtn.IsEnabled = !busy;
        JoinBtn.IsEnabled   = !busy;

        if (busy)
        {
            _opCts?.Cancel();
            _opCts = new CancellationTokenSource();
            CancelOpBtn.Visibility = Visibility.Visible;
        }
        else
        {
            CancelOpBtn.Visibility = Visibility.Collapsed;
            _opCts?.Cancel();
            _opCts = null;
        }
    }

    private ushort GetPort()
    {
        if (ushort.TryParse(PortBox.Text.Trim(), out ushort p) && p > 0) return p;
        PortBox.Text = "42777";
        return 42777;
    }

    private void Dispatch(Action a)
    {
        if (Dispatcher.CheckAccess()) a();
        else Dispatcher.Invoke(a);
    }

    /// <summary>
    /// Shows a modal dialog without blocking whoever raised the event. These fire
    /// from the network poll thread via <see cref="Dispatch"/>, which uses a
    /// blocking Invoke — a modal box there stalls packet handling until dismissed.
    /// </summary>
    private void Announce(string message, string title, MessageBoxImage icon)
        => Dispatcher.BeginInvoke(new Action(async () =>
            await ShowModal(title, message, "باشه", null, icon == MessageBoxImage.Warning)));

    private void CheckUpdate_Click(object sender, RoutedEventArgs e) => UpdateService.CheckNow();

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _memberTimer.Stop();
        _screenShare.Dispose();
        StopAudioPlayback();
        _trayIcon?.Dispose();
        _file.Dispose();
        _voice.Dispose();
        _chat.Dispose();
        _room.Dispose();
    }
}

/// <summary>One row in the member sidebar, with the host's moderation affordances.</summary>
public sealed class MemberItem : INotifyPropertyChanged
{
    public required string Username    { get; init; }
    public          bool   IsSelf      { get; init; }
    public          bool   IsHostRole  { get; init; }
    public          bool   CanModerate { get; init; }

    public string     Initial       => Username.TrimStart() is { Length: > 0 } s ? s[..1].ToUpperInvariant() : "؟";
    public Visibility ModVisibility => CanModerate ? Visibility.Visible : Visibility.Collapsed;

    public string     RoleLabel      => IsSelf ? "شما" : IsHostRole ? "میزبان" : "";
    public Visibility RoleVisibility => RoleLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            Notify();
            Notify(nameof(MuteIcon));
            Notify(nameof(MuteTip));
        }
    }

    public PackIconLucideKind MuteIcon => _isMuted ? PackIconLucideKind.MicOff : PackIconLucideKind.Mic;
    public string             MuteTip  => _isMuted ? "خارج کردن از میوت" : "میوت کردن";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Represents a file share event shown as a card in the chat panel.</summary>
public class FileNotification : INotifyPropertyChanged
{
    private string _status      = "";
    private bool   _canDownload = true;

    public string Id       { get; init; } = "";
    public string FileName { get; init; } = "";
    public string SizeText { get; init; } = "";
    public bool   IsSent   { get; init; }

    public Visibility DownloadVisibility => IsSent ? Visibility.Collapsed : Visibility.Visible;

    public string Status
    {
        get => _status;
        set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); }
    }

    public bool CanDownload
    {
        get => _canDownload;
        set { _canDownload = value; PropertyChanged?.Invoke(this, new(nameof(CanDownload))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
