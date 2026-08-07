using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NAudio.Wave;
// Explicit aliases resolve WinForms vs WPF conflicts
using Clipboard      = System.Windows.Clipboard;
using Color          = System.Windows.Media.Color;
using Colors         = System.Windows.Media.Colors;
using Key            = System.Windows.Input.Key;
using KeyEventArgs   = System.Windows.Input.KeyEventArgs;
using MessageBox     = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using VirtualLANPlatform.Core.Chat;
using VirtualLANPlatform.Core.FileTransfer;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;
using VirtualLANPlatform.Core.Room;
using VirtualLANPlatform.Core.ScreenShare;
using VirtualLANPlatform.Core.Services;
using VirtualLANPlatform.Core.Storage;
using VirtualLANPlatform.Core.VirtualNetwork;
using VirtualLANPlatform.Core.Voice;

namespace VirtualLANPlatform.UI.Views;

public partial class TestWindow : Window
{
    private readonly DatabaseManager       _db   = new();
    private readonly P2PManager            _p2p  = new();
    private readonly VirtualNetworkManager _vnet = new();
    private readonly RoomManager           _room;
    private readonly ChatManager           _chat;
    private readonly VoiceManager          _voice;
    private readonly FileManager           _file;

    private readonly Dictionary<string, FileNotification> _fileNotifs = new();

    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _busy;

    private CancellationTokenSource? _copyCodeCts;
    private CancellationTokenSource? _copyVipCts;
    private CancellationTokenSource? _saveUserCts;
    private readonly System.Windows.Threading.DispatcherTimer _memberTimer;

    private readonly ScreenShareManager _screenShare = new();
    private string?                     _remoteSharerUsername;
    private Storyboard?                 _dotPulse;
    private WaveOutEvent?               _audioOut;
    private BufferedWaveProvider?       _audioBuffer;

    private static readonly string UsernamePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VirtualLANPlatform", "username.txt");

    [DllImport("user32.dll")] private static extern bool MessageBeep(uint uType);

    public TestWindow()
    {
        InitializeComponent();
        _room  = new RoomManager(_p2p, _vnet, _db);
        _chat  = new ChatManager(_p2p);
        _voice = new VoiceManager(_p2p);
        _file  = new FileManager(_p2p);

        _memberTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(1) };
        _memberTimer.Tick += (_, _) => RefreshMemberList();
        _memberTimer.Start();

        InitTrayIcon();
        WireEvents();
        ShowWinTunVersion();
        LoadSavedUsername();
        LoadWindowIcon();
    }

    // ── Username persistence ──────────────────────────────────────────────────

    private void LoadWindowIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/logo.png");
            Icon = new System.Windows.Media.Imaging.BitmapImage(uri);
        }
        catch { }
    }

    private void LoadSavedUsername()
    {
        try
        {
            if (System.IO.File.Exists(UsernamePath))
            {
                string saved = System.IO.File.ReadAllText(UsernamePath).Trim();
                if (saved.Length > 0) UsernameBox.Text = saved;
            }
        }
        catch { }
    }

    private static void SaveUsername(string username)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(UsernamePath)!);
            System.IO.File.WriteAllText(UsernamePath, username);
        }
        catch { }
    }

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
            else
            {
                MessageBeep(0x00000040); // MB_ICONASTERISK fallback
            }
        }
        catch { }
    }

    // ── Room navigation ───────────────────────────────────────────────────────

    private void EnterRoom(string code, bool isHost)
    {
        LobbyActions.Visibility     = Visibility.Collapsed;
        LobbyHeaderPanel.Visibility = Visibility.Collapsed;
        Footer.Visibility           = Visibility.Collapsed;

        RoomHeaderPanel.Opacity         = 0;
        RoomHeaderPanel.RenderTransform = new System.Windows.Media.TranslateTransform(18, 0);
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

        CodeDisplay.Text      = string.IsNullOrEmpty(code) ? "—" : code;
        CopyCodeBtn.IsEnabled = !string.IsNullOrEmpty(code);

        LeaveCloseBtn.Content   = isHost ? "بستن Room" : "خروج از Room";
        LeaveCloseBtn.IsEnabled = true;

        HostVipDisplay.Text = RoomManager.HostVIP;

        SetStatus(isHost ? "Host — منتظر اتصال" : "متصل", "#43B581");
        Log($"وارد Room شدید — نقش: {(isHost ? "Host" : "Guest")}");
        TabChat_Click(null!, null!);
    }

    private void LeaveRoom()
    {
        LobbyActions.Visibility    = Visibility.Visible;
        LobbyHeaderPanel.Visibility = Visibility.Visible;
        RoomHeaderPanel.Visibility  = Visibility.Collapsed;
        Footer.Visibility           = Visibility.Visible;

        CodeDisplay.Text      = "—";
        CopyCodeBtn.IsEnabled = false;
        LeaveCloseBtn.IsEnabled = false;

        MicBtn.IsEnabled         = false;
        SpeakerBtn.IsEnabled     = false;
        SendFileBtn.IsEnabled    = false;
        ScreenShareBtn.IsEnabled = false;

        if (_screenShare.IsSharing)
        {
            _screenShare.Stop();
            _room.BroadcastScreenShareStop();
            ScreenShareBtn.Content    = "🖥  اشتراک صفحه";
            ScreenShareBtn.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x30, 0x35));
        }
        StopAudioPlayback();
        _remoteSharerUsername         = null;
        ScreenSharePanel.Visibility   = Visibility.Collapsed;
        TabScreenBtn.Visibility       = Visibility.Collapsed;
        ScreenFrameImage.Source       = null;

        MicBtn.Content        = "🎤 میکروفون";
        SpeakerBtn.Content    = "🔊 اسپیکر";
        MicBtn.Background     = new SolidColorBrush(Color.FromRgb(0x43, 0xB5, 0x81));
        SpeakerBtn.Background = new SolidColorBrush(Color.FromRgb(0x43, 0xB5, 0x81));

        MemberList.Items.Clear();
        ChatList.Items.Clear();
        _fileNotifs.Clear();
        VoiceStatus.Text = "—";

        ResetDebug();
        SetStatus("آماده", "#747F8D");
        TabLog_Click(null!, null!);
    }

    // ── Event wiring ──────────────────────────────────────────────────────────

    private void WireEvents()
    {
        _room.StatusChanged += msg => Dispatch(() => { StatusLabel.Text = msg; Log(msg); });


        _p2p.StatusChanged += msg => Dispatch(() => { StatusLabel.Text = msg; Log(msg); });

        _p2p.PeerConnected += info => Dispatch(() =>
        {
            DbgPeers.Text = _p2p.PeerCount.ToString();
            SetStatus("متصل", "#43B581");
            MicBtn.IsEnabled         = true;
            SpeakerBtn.IsEnabled     = true;
            SendFileBtn.IsEnabled    = true;
            ScreenShareBtn.IsEnabled = true;
            Log($"Peer متصل: {info.Username} ({info.EndPoint})");
            RefreshMemberList();
        });

        _p2p.EncryptionEstablished += (peerId, ok) => Dispatch(() =>
        {
            if (ok)
            {
                DbgEncryption.Text       = "🔒 AES-256-GCM";
                DbgEncryption.Foreground = new SolidColorBrush(Color.FromRgb(0x43, 0xB5, 0x81));
                Log($"رمزنگاری برقرار شد — Peer {peerId}");
            }
            else
            {
                DbgEncryption.Text       = "⚠ خطای کلید";
                DbgEncryption.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x47, 0x47));
            }
        });

        _p2p.PeerDisconnected += (id, reason) => Dispatch(() =>
        {
            DbgPeers.Text = _p2p.PeerCount.ToString();
            Log($"Peer قطع: {reason}");
            if (_p2p.PeerCount == 0 && _room.IsActive)
            {
                SetStatus("قطع شده", "#747F8D");
                MicBtn.IsEnabled         = false;
                SpeakerBtn.IsEnabled     = false;
                SendFileBtn.IsEnabled    = false;
                ScreenShareBtn.IsEnabled = false;
            }
            RefreshMemberList();
        });

        _p2p.ConnectionFailed += (title, msg) => Dispatch(() =>
        {
            SetStatus("خطا", "#F04747");
            SetBusy(false);
            Log($"خطا: {title} — {msg}");
            MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        });

        _p2p.ConnectionRejected += reason => Dispatch(() =>
        {
            SetBusy(false);
            Log($"اتصال رد شد: {reason}");
            MessageBox.Show(reason, "اتصال رد شد", MessageBoxButton.OK, MessageBoxImage.Warning);
        });

        _p2p.MessageReceived += (id, frame) => Dispatch(() =>
        {
            if (frame.Type != MessageType.VirtualLanPacket &&
                frame.Type != MessageType.TextChat &&
                frame.Type != MessageType.VoiceData)
                Log($"پیام [{frame.Type}] از Peer {id}");
            RefreshMemberList();
        });

        _room.MemberJoined += m => Dispatch(() =>
        {
            Log($"عضو جدید: {m.Username}  IP: {m.VirtualIP}");
            RefreshMemberList();
        });

        _room.MemberLeft += m => Dispatch(() =>
        {
            Log($"عضو خارج شد: {m.Username}");
            RefreshMemberList();
        });

        _room.RoomClosed += msg => Dispatch(() =>
        {
            Log(msg);
            _room.Shutdown();
            _voice.Reset();
            LeaveRoom();
        });

        _vnet.StatusChanged += msg => Dispatch(() =>
        {
            DbgVLanStatus.Text = msg;
            DbgVirtualIP.Text  = _room.MyVIP ?? "—";
            Log($"[VNet] {msg}");
        });

        // ── Screen Share ──────────────────────────────────────────────────────
        _screenShare.FrameCaptured += bytes =>
        {
            _room.BroadcastScreenShareFrame(bytes);
            Dispatch(() => ShowScreenFrame(bytes));
        };

        _screenShare.AudioCaptured += (pcm, fmt) => SendAudio(pcm, fmt);

        _room.ScreenShareAudioReceived += packet => Dispatch(() => PlayAudio(packet));

        _room.ScreenShareStarted += username => Dispatch(() =>
        {
            _remoteSharerUsername         = username;
            ScreenSharerLabel.Text        = $"صفحه‌نمایش  {username}";
            ScreenSharePanel.Visibility   = Visibility.Visible;
            TabScreenBtn.Visibility       = Visibility.Visible;
            ScreenShareBtn.IsEnabled      = false; // only the sharer can stop their own share
            TabScreen_Click(null!, null!);
            Log($"اشتراک صفحه توسط {username} شروع شد");
        });

        _room.ScreenShareFrame += (_, bytes) => Dispatch(() => ShowScreenFrame(bytes));

        _room.ScreenShareStopped += username => Dispatch(() =>
        {
            _remoteSharerUsername         = null;
            ScreenSharePanel.Visibility   = Visibility.Collapsed;
            TabScreenBtn.Visibility       = Visibility.Collapsed;
            StopAudioPlayback();
            if (_p2p.PeerCount > 0) ScreenShareBtn.IsEnabled = true;
            TabChat_Click(null!, null!);
            Log($"اشتراک صفحه توسط {username} متوقف شد");
        });

        // ── Chat ──────────────────────────────────────────────────────────────
        _chat.MessageReceived += msg => Dispatch(() => AddChatMessage(msg));

        // ── Voice ─────────────────────────────────────────────────────────────
        _voice.MicChanged += active => Dispatch(() =>
        {
            MicBtn.Content    = active ? "🎤 میکروفون" : "🔇 میکروفون";
            MicBtn.Background = new SolidColorBrush(active
                ? Color.FromRgb(0x43, 0xB5, 0x81)
                : Color.FromRgb(0xED, 0x42, 0x45));
        });

        _voice.SpeakerChanged += active => Dispatch(() =>
        {
            SpeakerBtn.Content    = active ? "🔊 اسپیکر" : "🔇 اسپیکر";
            SpeakerBtn.Background = new SolidColorBrush(active
                ? Color.FromRgb(0x43, 0xB5, 0x81)
                : Color.FromRgb(0xED, 0x42, 0x45));
        });

        _voice.StatusChanged += msg => Dispatch(() => VoiceStatus.Text = msg);

        // ── File transfer ──────────────────────────────────────────────────────

        _file.IncomingFile += info => Dispatch(() =>
        {
            string sizeText = info.Size < 1024 * 1024
                ? $"{info.Size / 1024.0:F1} KB"
                : $"{info.Size / (1024.0 * 1024):F1} MB";

            var notif = new FileNotification
            {
                Id          = info.Id,
                FileName    = info.FileName,
                SizeText    = sizeText,
                IsSent      = false,
                Status      = "آماده دانلود",
                CanDownload = true
            };
            _fileNotifs[info.Id] = notif;
            ChatList.Items.Add(notif);
            ChatList.ScrollIntoView(notif);
            TabChat_Click(null!, null!);
            this.Activate();
            Log($"[فایل] فایل دریافتی: {info.FileName}  ({sizeText})");
            VoiceStatus.Text = $"📎 فایل دریافتی: {info.FileName}";
        });

        _file.TransferAccepted += id => Dispatch(() =>
        {
            if (_fileNotifs.TryGetValue(id, out var notif) && notif.IsSent)
            {
                notif.Status = "در حال ارسال...";
                Log("[فایل] گیرنده پذیرفت — در حال ارسال");
            }
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
                Log($"[فایل] ✓ {(sent ? "ارسال" : "دریافت")} کامل: {name}");
                VoiceStatus.Text = sent ? $"ارسال شد: {name}" : $"دانلود شد: {name}";

                if (!sent)
                {
                    PlayDownloadSound();
                    ShowNotification("دانلود کامل شد", $"✓ {name}  →  Downloads");
                }
            }
            else
            {
                Log($"[فایل] ✓ کامل: {name}  →  {path}");
                VoiceStatus.Text = $"کامل شد: {name}";
            }
        });

        _file.TransferFailed += (id, reason) => Dispatch(() =>
        {
            if (_fileNotifs.TryGetValue(id, out var notif))
            {
                notif.Status      = notif.IsSent ? $"✗ ارسال ناموفق: {reason}" : $"✗ خطا: {reason}";
                notif.CanDownload = false;
                _fileNotifs.Remove(id);
            }
            Log($"[فایل] ✗ خطا: {reason}");
            VoiceStatus.Text = $"خطا: {reason}";
        });

        _file.StatusChanged += msg => Dispatch(() =>
        {
            Log($"[فایل] {msg}");
            VoiceStatus.Text = msg;
        });
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    private void ShowWinTunVersion()
    {
        try
        {
            uint v = VirtualAdapter.GetDriverVersion();
            // v==0 means DLL loaded but no adapter active yet — driver loads on first connect
            DbgWinTun.Text = v == 0 ? "آماده" : $"{v >> 16}.{v & 0xFFFF}";
        }
        catch { DbgWinTun.Text = "DLL یافت نشد"; }
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private async void CreateRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        SetStatus("در حال ایجاد Room...", "#FAA61A");

        string username = UsernameBox.Text.Trim() is { Length: > 0 } u ? u : "Host";
        SaveUsername(username);
        _chat.SetUsername(username);

        var (ok, lanCode, _) = await _room.CreateRoomAsync(username, port: 42777);
        if (ok)
        {
            DbgRole.Text = "Host";
            UpdateP2PDebug();
            RefreshMemberList();
            EnterRoom(lanCode, isHost: true);
        }
        else
        {
            SetStatus("خطا در ایجاد Room", "#F04747");
        }
        SetBusy(false);
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string code = JoinCodeBox.Text.Trim();
        if (code.Length == 0)
        {
            MessageBox.Show("لطفاً Connection Code را وارد کنید.", "خطا",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        SetStatus("در حال اتصال...", "#FAA61A");
        Log($"تلاش اتصال: {code}");

        string username = UsernameBox.Text.Trim() is { Length: > 0 } u ? u : "Guest";
        SaveUsername(username);
        _chat.SetUsername(username);

        bool ok = await _room.JoinRoomAsync(code, username);
        if (ok)
        {
            DbgRole.Text = "Guest";
            RefreshMemberList();
            EnterRoom(code, isHost: false);
        }
        SetBusy(false);
    }

    private async void CopyHostVip_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetDataObject(HostVipDisplay.Text, copy: true); }
        catch { try { Clipboard.SetText(HostVipDisplay.Text); } catch { } }

        _copyVipCts?.Cancel();
        _copyVipCts = new CancellationTokenSource();
        var cts = _copyVipCts;

        CopyHostVipBtn.Content    = "✓ کپی شد";
        CopyHostVipBtn.Background = new SolidColorBrush(Color.FromRgb(0x43, 0xB5, 0x81));
        try
        {
            await Task.Delay(1800, cts.Token);
            CopyHostVipBtn.Content = "کپی IP";
            CopyHostVipBtn.ClearValue(System.Windows.Controls.Button.BackgroundProperty);
        }
        catch (OperationCanceledException) { }
    }

    private async void SaveUsername_Click(object sender, RoutedEventArgs e)
    {
        string username = UsernameBox.Text.Trim();
        if (username.Length == 0) return;
        SaveUsername(username);

        _saveUserCts?.Cancel();
        _saveUserCts = new CancellationTokenSource();
        var cts = _saveUserCts;

        SaveUsernameBtn.Content    = "ذخیره شد ✓";
        SaveUsernameBtn.Background = new SolidColorBrush(Color.FromRgb(0x43, 0xB5, 0x81));
        try
        {
            await Task.Delay(1800, cts.Token);
            SaveUsernameBtn.Content = "ذخیره";
            SaveUsernameBtn.ClearValue(System.Windows.Controls.Button.BackgroundProperty);
        }
        catch (OperationCanceledException) { }
    }

    private async void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        string code = CodeDisplay.Text;
        if (string.IsNullOrEmpty(code) || code == "—") return;

        try { Clipboard.SetDataObject(code, copy: true); }
        catch { try { Clipboard.SetText(code); } catch { } }

        _copyCodeCts?.Cancel();
        _copyCodeCts = new CancellationTokenSource();
        var cts = _copyCodeCts;

        CopyCodeBtn.Content    = "✓ کپی شد";
        CopyCodeBtn.Background = new SolidColorBrush(Color.FromRgb(0x43, 0xB5, 0x81));
        Log($"Connection Code کپی شد: {code}");
        try
        {
            await Task.Delay(2000, cts.Token);
            CopyCodeBtn.Content = "کپی کد";
            CopyCodeBtn.ClearValue(System.Windows.Controls.Button.BackgroundProperty);
        }
        catch (OperationCanceledException) { }
    }

    private async void LeaveClose_Click(object sender, RoutedEventArgs e)
    {
        LeaveCloseBtn.IsEnabled = false;
        if (_room.IsHost)
            await _room.CloseRoomAsync();
        else
            await _room.LeaveRoomAsync();
        _voice.Reset();
        LeaveRoom();
        Log("از Room خارج شدید.");
    }

    // ── Voice ─────────────────────────────────────────────────────────────────

    private void Mic_Click(object sender, RoutedEventArgs e)     => _voice.ToggleMic();
    private void Speaker_Click(object sender, RoutedEventArgs e) => _voice.ToggleSpeaker();

    private void SendFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "انتخاب فایل برای ارسال" };
        if (dlg.ShowDialog() != true) return;
        string path = dlg.FileName;

        string transferId = Guid.NewGuid().ToString("N")[..12];
        string name       = System.IO.Path.GetFileName(path);
        long   size       = new System.IO.FileInfo(path).Length;

        if (size == 0)
        {
            MessageBox.Show($"فایل «{name}» خالی است (0 بایت) و قابل ارسال نیست.",
                "فایل خالی", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string sizeText = size < 1024 * 1024
            ? $"{size / 1024.0:F1} KB"
            : $"{size / (1024.0 * 1024):F1} MB";

        var notif = new FileNotification
        {
            Id = transferId, FileName = name, SizeText = sizeText,
            IsSent = true, Status = "در انتظار پذیرش...", CanDownload = false
        };
        _fileNotifs[transferId] = notif;
        ChatList.Items.Add(notif);
        ChatList.ScrollIntoView(notif);
        TabChat_Click(null!, null!);

        _ = Task.Run(() => _file.SendFileAsync(path, transferId));
    }

    private void DownloadFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not FileNotification notif) return;
        notif.CanDownload = false;
        notif.Status      = "در حال دانلود...";
        string savePath = _file.GetSavePath(notif.FileName);
        _file.AcceptTransfer(notif.Id, savePath);
    }

    // ── Chat ──────────────────────────────────────────────────────────────────

    private void TabLog_Click(object sender, RoutedEventArgs e)
    {
        LogList.Visibility        = Visibility.Visible;
        ChatList.Visibility       = Visibility.Collapsed;
        ScreenSharePanel.Visibility = Visibility.Collapsed;
        ChatInputArea.Visibility  = Visibility.Collapsed;
        SetTabHighlight(TabLogBtn);
    }

    private void TabChat_Click(object sender, RoutedEventArgs e)
    {
        LogList.Visibility        = Visibility.Collapsed;
        ChatList.Visibility       = Visibility.Visible;
        ScreenSharePanel.Visibility = Visibility.Collapsed;
        ChatInputArea.Visibility  = Visibility.Visible;
        SetTabHighlight(TabChatBtn);
        ChatInput.Focus();
    }

    private void TabScreen_Click(object sender, RoutedEventArgs e)
    {
        LogList.Visibility        = Visibility.Collapsed;
        ChatList.Visibility       = Visibility.Collapsed;
        ScreenSharePanel.Visibility = Visibility.Visible;
        ChatInputArea.Visibility  = Visibility.Collapsed;
        SetTabHighlight(TabScreenBtn);
    }

    private void SetTabHighlight(System.Windows.Controls.Button active)
    {
        var on  = new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2));
        var off = new SolidColorBrush(Colors.Transparent);
        var white = new SolidColorBrush(Colors.White);
        var dim   = new SolidColorBrush(Color.FromRgb(0xB9, 0xBB, 0xBE));

        foreach (var btn in new[] { TabLogBtn, TabChatBtn, TabScreenBtn })
        {
            btn.BorderBrush = btn == active ? on  : off;
            btn.Foreground  = btn == active ? white : dim;
        }
    }

    private void SendChat_Click(object sender, RoutedEventArgs e) => DoSendChat();

    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DoSendChat();
    }

    private void DoSendChat()
    {
        string text = ChatInput.Text.Trim();
        if (text.Length == 0) return;
        if (!_p2p.IsRunning)
        {
            MessageBox.Show("ابتدا به Room متصل شوید.", "خطا",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _chat.SendToAll(text);
        ChatInput.Clear();
    }

    private void AddChatMessage(ChatMessage msg)
    {
        ChatList.Items.Add(msg);
        ChatList.ScrollIntoView(msg);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RefreshMemberList()
    {
        // ‪ = LTR Embedding, ‬ = Pop — prevents Vazir RTL font from showing dots as slashes
        var members  = _room.GetMembers().OrderBy(m => m.Username).ToList();
        var newItems = members.Select(m => $"{m.Username}  •  ‪{m.VirtualIP}‬").ToList();

        // Skip rebuild if nothing changed — prevents animation flicker on every timer tick
        if (MemberList.Items.Count == newItems.Count &&
            newItems.Select((t, i) => (string)MemberList.Items[i] == t).All(x => x))
            return;

        MemberList.Items.Clear();
        foreach (var item in newItems)
            MemberList.Items.Add(item);

        DbgPeers.Text = (members.Count - (_room.IsHost ? 1 : 0)).ToString();
    }

    private void UpdateP2PDebug()
    {
        var s = _p2p.NatStatus;
        DbgLocalIP.Text   = s.LocalIP   ?? "—";
        DbgPublicIP.Text  = s.PublicIP  ?? "—";
        DbgLocalPort.Text = s.LocalPort?.ToString() ?? "—";
        DbgExtPort.Text   = s.ExternalPort?.ToString() ?? "—";
        DbgNatType.Text   = s.NatType;
    }

    private void ResetDebug()
    {
        DbgLocalIP.Text = DbgPublicIP.Text = DbgLocalPort.Text =
        DbgExtPort.Text = DbgNatType.Text  = DbgRole.Text = "—";
        DbgPeers.Text         = "0";
        DbgVirtualIP.Text     = "—";
        DbgVLanStatus.Text    = "غیرفعال";
        DbgEncryption.Text       = "🔓 بدون رمز";
        DbgEncryption.Foreground = new SolidColorBrush(Color.FromRgb(0xFA, 0xA6, 0x1A));
    }

    private void SetStatus(string text, string hexColor)
    {
        StatusLabel.Text = text;
        var c = Color.FromArgb(255,
            Convert.ToByte(hexColor[1..3], 16),
            Convert.ToByte(hexColor[3..5], 16),
            Convert.ToByte(hexColor[5..7], 16));
        StatusDot.Color = c;

        _dotPulse?.Stop();
        bool connected = hexColor == "#43B581";
        if (connected)
        {
            var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var anim = new DoubleAnimation(1.0, 1.35, new Duration(TimeSpan.FromSeconds(0.7)))
            {
                AutoReverse = true,
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
    }

    private void ScreenShare_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteSharerUsername != null) return;

        if (_screenShare.IsSharing)
        {
            _screenShare.Stop();
            _room.BroadcastScreenShareStop();
            StopAudioPlayback();
            ScreenShareBtn.Content      = "🖥  اشتراک صفحه";
            ScreenShareBtn.ClearValue(System.Windows.Controls.Button.BackgroundProperty);
            ScreenSharePanel.Visibility = Visibility.Collapsed;
            TabScreenBtn.Visibility     = Visibility.Collapsed;
            ScreenFrameImage.Source     = null;
            TabChat_Click(null!, null!);
            Log("اشتراک صفحه متوقف شد");
        }
        else
        {
            var picker = new WindowPickerDialog { Owner = this };
            if (picker.ShowDialog() != true) return;

            _room.BroadcastScreenShareStart();
            _screenShare.Start(fps: 8,
                windowHandle: picker.SelectedHandle,
                shareAudio:   picker.ShareAudio);

            ScreenShareBtn.Content      = "⏹  توقف اشتراک";
            ScreenShareBtn.Background   = new SolidColorBrush(Color.FromRgb(0xED, 0x42, 0x45));
            ScreenSharerLabel.Text      = "صفحه‌نمایش من";
            ScreenSharePanel.Visibility = Visibility.Visible;
            TabScreenBtn.Visibility     = Visibility.Visible;
            TabScreen_Click(null!, null!);
            Log($"اشتراک صفحه شروع شد{(picker.ShareAudio ? " (با صدا)" : "")}");
        }
    }

    private void ShowScreenFrame(byte[] jpegBytes)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = new System.IO.MemoryStream(jpegBytes);
            bmp.EndInit();
            bmp.Freeze();
            ScreenFrameImage.Source = bmp;
        }
        catch { }
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
        int  sampleRate    = BitConverter.ToInt32(packet, 0);
        int  channels      = BitConverter.ToInt16(packet, 4);
        int  bitsPerSample = BitConverter.ToInt16(packet, 6);
        int  encoding      = BitConverter.ToInt16(packet, 8);

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

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CreateBtn.IsEnabled = !busy;
        JoinBtn.IsEnabled   = !busy;
    }

    private void Log(string msg)
        => LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");

    private void Dispatch(Action a)
    {
        if (Dispatcher.CheckAccess()) a();
        else Dispatcher.Invoke(a);
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
        => UpdateService.CheckNow();

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _screenShare.Dispose();
        StopAudioPlayback();
        _trayIcon?.Dispose();
        _file.Dispose();
        _voice.Dispose();
        _chat.Dispose();
        _room.Dispose();
    }
}

/// <summary>Represents a file share event shown as a card in the chat panel.</summary>
public class FileNotification : System.ComponentModel.INotifyPropertyChanged
{
    private string _status      = "";
    private bool   _canDownload = true;

    public string     Id       { get; init; } = "";
    public string     FileName { get; init; } = "";
    public string     SizeText { get; init; } = "";
    public bool       IsSent   { get; init; }

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

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
