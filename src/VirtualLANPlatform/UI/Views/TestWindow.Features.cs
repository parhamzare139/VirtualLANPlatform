using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MahApps.Metro.IconPacks;
using VirtualLANPlatform.Core.Services;
using VirtualLANPlatform.Core.Voice;
using VirtualLANPlatform.UI.Localization;
using Brush     = System.Windows.Media.Brush;
using Button    = System.Windows.Controls.Button;
using CheckBox  = System.Windows.Controls.CheckBox;
using ComboBox  = System.Windows.Controls.ComboBox;
using Clipboard = System.Windows.Clipboard;
using Color     = System.Windows.Media.Color;
using Key       = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace VirtualLANPlatform.UI.Views;

/// <summary>
/// The "make it feel like a real product" layer: settings page, tray menu, presence,
/// typing, unread counts, the virtual-LAN roster and traffic meter, bug reports and
/// the what's-new note. Split out of the main file, which is about the core flows.
/// </summary>
public partial class TestWindow
{
    private readonly AppSettings     _settings = AppSettings.I;
    private readonly PresenceMonitor _presence = new();
    private readonly PushToTalkHook  _ptt      = new();

    private static readonly string AppVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    // ── Startup hooks (called from the constructor / Loaded) ─────────────────

    private void InitFeatures()
    {
        AppLog.Prune();
        AppLog.Info("app", $"started v{AppVersion} lang={Loc.I.Language} admin={IsRunningAsAdmin()}");
        AboutVersion.Text = "v" + AppVersion;

        // Presence: what we are doing, pushed to whoever we are connected to.
        _presence.Changed += p => Dispatch(() =>
        {
            if (!_settings.ShareStatus) return;
            if (_room.IsActive) _room.SetMyPresence(p);
            if (_vlan.IsActive) _vlan.SetMyPresence(p);
            RefreshMemberList();
            RenderVlanMembers();
        });
        _room.PresenceChanged += (_, _) => Dispatch(RefreshMemberList);

        // Typing indicator.
        _chat.TypingChanged += (user, typing) => Dispatch(() => SetTyping(user, typing));

        // Virtual LAN roster.
        _vlan.MembersChanged += () => Dispatch(RenderVlanMembers);
        _vlan.MemberJoined += (name, ip) => Dispatch(() =>
        {
            AppSounds.Play(AppSound.Join);
            Notify(Loc.T("Vlan_MemberJoinedTitle"), Loc.T("Vlan_MemberJoinedBody", name, ip), ToastKind.Success, alsoTray: true);
        });
        _vlan.MemberLeft += (name, _) => Dispatch(() =>
        {
            AppSounds.Play(AppSound.Leave);
            Notify(Loc.T("Vlan_MemberLeftTitle"), Loc.T("Vlan_MemberLeftBody", name), ToastKind.Info, alsoTray: true);
        });
        _vlan.Connected    += _ => Dispatch(() => { AppSounds.Play(AppSound.Connected); StartVlanStats(); RenderVlanMembers(); });
        _vlan.Disconnected += ()  => Dispatch(StopVlanStats);

        InitPushToTalk();

        Activated    += (_, _) => { ClearUnreadIfVisible(); };
        StateChanged += (_, _) => { ClearUnreadIfVisible(); };
    }

    /// <summary>Runs once the window handle exists: the hooks need a message loop.</summary>
    private void InitFeaturesLoaded()
    {
        _ptt.Install();
        if (App.StartHidden) HideToTray(silent: true);
    }

    private void DisposeFeatures()
    {
        try { _ptt.Dispose(); }      catch { }
        try { _presence.Dispose(); } catch { }
        _meterTimer?.Stop();
        _vlanStatsTimer?.Stop();
    }

    // ── Presence helpers ─────────────────────────────────────────────────────

    public static Brush PresenceBrush(Presence p) => p switch
    {
        Presence.InGame => new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)),
        Presence.Away   => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        _               => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E))
    };

    public static string PresenceLabel(Presence p) => Loc.T(p switch
    {
        Presence.InGame => "Presence_InGame",
        Presence.Away   => "Presence_Away",
        _               => "Presence_Online"
    });

    // ── Typing indicator ─────────────────────────────────────────────────────

    private readonly Dictionary<string, DateTime> _typing = new(StringComparer.Ordinal);
    private static readonly TimeSpan TypingTtl = TimeSpan.FromSeconds(5);

    private void SetTyping(string user, bool typing)
    {
        if (typing) _typing[user] = DateTime.UtcNow;
        else        _typing.Remove(user);
        RenderTyping();
    }

    /// <summary>Called from the 1 s member timer: a "typing" that never got its "stopped" ages out.</summary>
    private void ExpireTyping()
    {
        if (_typing.Count == 0) return;
        var cutoff = DateTime.UtcNow - TypingTtl;
        var stale  = _typing.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
        if (stale.Count == 0) return;
        foreach (var k in stale) _typing.Remove(k);
        RenderTyping();
    }

    private void RenderTyping()
    {
        if (_typing.Count == 0 || !_room.IsActive)
        {
            TypingPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TypingText.Text = _typing.Count == 1
            ? Loc.T("Chat_Typing", _typing.Keys.First())
            : Loc.T("Chat_TypingMany", _typing.Count);
        TypingPanel.Visibility = Visibility.Visible;
    }

    // ── Unread badge ─────────────────────────────────────────────────────────

    private int _unreadChat;

    private bool ChatIsOnScreen =>
        IsVisible && IsActive && WindowState != WindowState.Minimized && _activeTab == "chat";

    private void CountUnread()
    {
        _unreadChat++;
        RenderUnread();
        FlashTaskbar();
    }

    private void ClearUnreadIfVisible()
    {
        if (_unreadChat == 0 || !ChatIsOnScreen) return;
        _unreadChat = 0;
        RenderUnread();
    }

    private void RenderUnread()
    {
        ChatUnreadBadge.Visibility = _unreadChat > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatUnreadText.Text        = _unreadChat > 99 ? "99+" : _unreadChat.ToString();
        Title = _unreadChat > 0 ? $"({_unreadChat}) Virtual LAN Platform" : "Virtual LAN Platform";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    /// <summary>Taskbar flash until the window is focused — the one cue every Windows user knows.</summary>
    private void FlashTaskbar()
    {
        if (IsActive || !IsVisible) return;
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            var fi = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = h,
                dwFlags = 0x00000002 | 0x0000000C, // FLASHW_TRAY | FLASHW_TIMERNOFG
                uCount = 0, dwTimeout = 0
            };
            FlashWindowEx(ref fi);
        }
        catch { }
    }

    // ── Push-to-talk ─────────────────────────────────────────────────────────

    private void InitPushToTalk()
    {
        _ptt.Key         = _settings.PttKey;
        _ptt.Enabled     = _settings.PushToTalk;
        _voice.PushToTalk = _settings.PushToTalk;

        _ptt.Changed += down =>
        {
            _voice.TalkKeyDown = down; // read by the capture thread; set here so the very next frame sees it
            Dispatch(() =>
            {
                if (_room.IsActive && _voice.IsMicActive)
                    AppSounds.Play(down ? AppSound.PttOn : AppSound.PttOff);
                SetMicVisual(_voice.IsMicActive);
            });
        };
        _ptt.Captured += vk => Dispatch(() =>
        {
            _settings.PttKey = vk;
            _settings.Save();
            _ptt.Key = vk;
            PttKeyBtn.Content = PushToTalkHook.KeyName(vk);
            SetMicVisual(_voice.IsMicActive);
        });
    }

    // ── Virtual LAN roster ───────────────────────────────────────────────────

    private void RenderVlanMembers()
    {
        if (!_vlan.IsActive)
        {
            VlanMemberList.Items.Clear();
            return;
        }

        var members = _vlan.Members;
        VlanMemberCount.Text     = members.Count.ToString();
        VlanAloneText.Visibility = members.Count <= 1 ? Visibility.Visible : Visibility.Collapsed;

        // Update rows in place when the line-up is unchanged: this runs every couple of
        // seconds for fresh pings, and rebuilding would make the list blink.
        var existing = VlanMemberList.Items.OfType<VlanMemberItem>().ToList();
        bool sameSet = existing.Count == members.Count &&
                       existing.Select(e => e.Ip).SequenceEqual(members.Select(m => m.Ip));
        if (sameSet)
        {
            for (int i = 0; i < members.Count; i++)
            {
                existing[i].Status = members[i].Status;
                existing[i].RttMs  = members[i].RttMs;
                existing[i].Name   = members[i].Name;
            }
            return;
        }

        VlanMemberList.Items.Clear();
        foreach (var m in members.OrderByDescending(m => m.IsHost).ThenBy(m => m.Name, StringComparer.Ordinal))
            VlanMemberList.Items.Add(new VlanMemberItem
            {
                Name = m.Name, Ip = m.Ip, IsHost = m.IsHost, IsSelf = m.IsSelf,
                Status = m.Status, RttMs = m.RttMs
            });
    }

    private async void VlanMemberIp_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string ip } || ip.Length == 0) return;
        try
        {
            Clipboard.SetText(ip);
            ShowToast(Loc.T("Common_Copied"), ip, ToastKind.Success);
        }
        catch { }
        await Task.CompletedTask;
    }

    // ── Virtual LAN traffic meter ────────────────────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _vlanStatsTimer;
    private (long Sent, long Recv, DateTime At)? _lastVlanStats;

    private void StartVlanStats()
    {
        _lastVlanStats = null;
        _vlanStatsTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _vlanStatsTimer.Tick -= VlanStatsTick;
        _vlanStatsTimer.Tick += VlanStatsTick;
        _vlanStatsTimer.Start();
        VlanStatsTick(null, EventArgs.Empty);
    }

    private void StopVlanStats()
    {
        _vlanStatsTimer?.Stop();
        _lastVlanStats = null;
        VlanPingText.Text = "—"; VlanUpText.Text = "0 KB/s"; VlanDownText.Text = "0 KB/s"; VlanLossText.Text = "0%";
        VlanQualityText.Text = "—";
        VlanQualityText.Foreground = Res("TxtMid");
        RenderVlanMembers();
    }

    private void VlanStatsTick(object? sender, EventArgs e)
    {
        if (!_vlan.IsActive) return;
        var (sent, recv, packets, loss) = _vlan.GetStatistics();
        var now = DateTime.UtcNow;

        double up = 0, down = 0;
        if (_lastVlanStats is { } last)
        {
            double secs = Math.Max(0.2, (now - last.At).TotalSeconds);
            up   = Math.Max(0, sent - last.Sent) / secs;
            down = Math.Max(0, recv - last.Recv) / secs;
        }
        _lastVlanStats = (sent, recv, now);

        var members = _vlan.Members;
        int rtt = _vlan.IsHost
            ? members.Where(m => !m.IsSelf).Select(m => m.RttMs).DefaultIfEmpty(0).Max()
            : _vlan.HostRttMs;
        double lossPct = packets > 0 ? loss * 100.0 / packets : 0;

        VlanPingLabel.Text = Loc.T(_vlan.IsHost ? "Vlan_PingWorst" : "Vlan_Ping");
        VlanPingText.Text  = members.Count > 1 && rtt >= 0 ? $"{rtt} ms" : "—";
        VlanUpText.Text    = Rate(up);
        VlanDownText.Text  = Rate(down);
        VlanLossText.Text  = $"{lossPct:0.#}%";

        if (members.Count <= 1)
        {
            VlanQualityText.Text = "—";
            VlanQualityText.Foreground = Res("TxtMid");
            return;
        }
        var (key, brush) = (rtt, lossPct) switch
        {
            ( < 60,  < 1) => ("Quality_Great", "Ok"),
            ( < 130, < 3) => ("Quality_Good",  "Ok"),
            ( < 220, < 8) => ("Quality_Fair",  "Warn"),
            _             => ("Quality_Poor",  "Danger")
        };
        VlanQualityText.Text       = Loc.T(key);
        VlanQualityText.Foreground = Res(brush);
    }

    private static string Rate(double bytesPerSec) => bytesPerSec switch
    {
        >= 1024 * 1024 => $"{bytesPerSec / (1024 * 1024):0.0} MB/s",
        >= 10 * 1024   => $"{bytesPerSec / 1024:0} KB/s",
        _              => $"{bytesPerSec / 1024:0.0} KB/s"
    };

    // ── Tray ─────────────────────────────────────────────────────────────────

    private bool _exitRequested;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;

    private void InitTrayMenu()
    {
        if (_trayIcon == null) return;

        _trayMenu = new System.Windows.Forms.ContextMenuStrip
        {
            Renderer  = new DarkMenuRenderer(),
            Font      = new System.Drawing.Font("Segoe UI", 9.5f),
            ShowImageMargin = false,
            BackColor = System.Drawing.Color.FromArgb(0x1A, 0x1F, 0x2C),
            ForeColor = System.Drawing.Color.FromArgb(0xE8, 0xEA, 0xF0)
        };
        _trayMenu.Opening += (_, _) => BuildTrayMenu();
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        _trayIcon.MouseClick  += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray(); };
    }

    /// <summary>Rebuilt on every open so it reflects the current state and language.</summary>
    private void BuildTrayMenu()
    {
        if (_trayMenu == null) return;
        _trayMenu.Items.Clear();
        _trayMenu.RightToLeft = Loc.I.Flow == System.Windows.FlowDirection.RightToLeft
            ? System.Windows.Forms.RightToLeft.Yes : System.Windows.Forms.RightToLeft.No;

        var show = new System.Windows.Forms.ToolStripMenuItem(Loc.T("Tray_Show"))
            { Font = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold) };
        show.Click += (_, _) => ShowFromTray();
        _trayMenu.Items.Add(show);

        if (_vlan.IsActive || _room.IsActive)
        {
            _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            if (_vlan.IsActive)
            {
                var stop = new System.Windows.Forms.ToolStripMenuItem(Loc.T("Tray_StopVlan"));
                stop.Click += (_, _) => Dispatch(() => VlanDisconnect_Click(this, new RoutedEventArgs()));
                _trayMenu.Items.Add(stop);
            }
            if (_room.IsActive)
            {
                var leave = new System.Windows.Forms.ToolStripMenuItem(Loc.T(_isRoomHost == true ? "Room_Close" : "Room_Leave"));
                leave.Click += (_, _) => Dispatch(() => { ShowFromTray(); LeaveClose_Click(this, new RoutedEventArgs()); });
                _trayMenu.Items.Add(leave);
            }
        }

        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var settings = new System.Windows.Forms.ToolStripMenuItem(Loc.T("Settings_Title"));
        settings.Click += (_, _) => Dispatch(() => { ShowFromTray(); OpenSettings(); });
        _trayMenu.Items.Add(settings);

        var exit = new System.Windows.Forms.ToolStripMenuItem(Loc.T("Tray_Exit"));
        exit.Click += (_, _) => Dispatch(ExitApp);
        _trayMenu.Items.Add(exit);
    }

    private void HideToTray(bool silent = false)
    {
        Hide();
        if (silent || _settings.TrayHintShown) return;
        ShowNotification(Loc.T("Tray_HintTitle"), Loc.T("Tray_HintBody"));
        _settings.TrayHintShown = true;
        _settings.Save();
    }

    private void ShowFromTray()
    {
        Dispatch(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false; // pull in front of a fullscreen game once, then let go
        });
    }

    private void ExitApp()
    {
        _exitRequested = true;
        Close();
    }

    /// <summary>The WinForms tray menu, painted to match the app instead of Windows 95.</summary>
    private sealed class DarkMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { }

        protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected
                ? System.Drawing.Color.White
                : System.Drawing.Color.FromArgb(0xE8, 0xEA, 0xF0);
            base.OnRenderItemText(e);
        }

        private sealed class DarkColors : System.Windows.Forms.ProfessionalColorTable
        {
            private static readonly System.Drawing.Color Bg     = System.Drawing.Color.FromArgb(0x1A, 0x1F, 0x2C);
            private static readonly System.Drawing.Color Hover  = System.Drawing.Color.FromArgb(0x2A, 0x31, 0x45);
            private static readonly System.Drawing.Color Line   = System.Drawing.Color.FromArgb(0x2E, 0x35, 0x48);
            public override System.Drawing.Color MenuItemSelected            => Hover;
            public override System.Drawing.Color MenuItemBorder              => Hover;
            public override System.Drawing.Color MenuBorder                  => Line;
            public override System.Drawing.Color ToolStripDropDownBackground => Bg;
            public override System.Drawing.Color ImageMarginGradientBegin    => Bg;
            public override System.Drawing.Color ImageMarginGradientMiddle   => Bg;
            public override System.Drawing.Color ImageMarginGradientEnd      => Bg;
            public override System.Drawing.Color SeparatorDark               => Line;
            public override System.Drawing.Color SeparatorLight              => Line;
        }
    }

    // ── Settings page ────────────────────────────────────────────────────────

    private bool _suppressSettingEvents;
    private System.Windows.Threading.DispatcherTimer? _meterTimer;
    private double _meterShown;

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void SettingsClose_Click(object sender, RoutedEventArgs e) => CloseSettings();

    // ── Update check, inline on the settings page ────────────────────────────
    // The full-screen gate would open *behind* the settings page and the answer would
    // only be seen after closing it. So the check runs here, in the About card, and
    // only the actual install hands over to the gate (which has the progress bar).

    private UpdateInfo? _settingsUpdate;
    private System.Windows.Media.Animation.Storyboard? _settingsSpin;

    private async void SettingsCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!SettingsUpdateBtn.IsEnabled) return;
        SettingsUpdateBtn.IsEnabled = false;
        SetSettingsUpdateStatus(PackIconLucideKind.RefreshCw, "TxtMid", Loc.T("Update_CheckingBody"));
        SettingsUpdateNowBtn.Visibility = Visibility.Collapsed;
        StartSettingsSpin();

        UpdateInfo info;
        try
        {
            var delay = Task.Delay(600);
            var check = UpdateService.CheckAsync(CancellationToken.None);
            await Task.WhenAll(delay, check);
            info = check.Result;
        }
        catch (Exception ex) { info = new UpdateInfo(UpdateState.Failed, Error: ex.Message); }
        finally
        {
            StopSettingsSpin();
            SettingsUpdateBtn.IsEnabled = true;
        }

        _settingsUpdate = info;
        switch (info.State)
        {
            case UpdateState.UpToDate:
                SetSettingsUpdateStatus(PackIconLucideKind.CircleCheck, "Ok", Loc.T("Settings_UpToDate", AppVersion));
                break;
            case UpdateState.Available:
                SetSettingsUpdateStatus(PackIconLucideKind.Sparkles, "Warn", Loc.T("Settings_UpdateAvailable", info.Latest, info.Current));
                SettingsUpdateNowBtn.Visibility = Visibility.Visible;
                break;
            default:
                SetSettingsUpdateStatus(PackIconLucideKind.CircleAlert, "Danger", Loc.T("Settings_UpdateFailed"));
                break;
        }
    }

    private void SetSettingsUpdateStatus(PackIconLucideKind icon, string brushKey, string text)
    {
        SettingsUpdateStatusIcon.Kind       = icon;
        SettingsUpdateStatusIcon.Foreground = Res(brushKey);
        SettingsUpdateStatus.Text           = text;
        SettingsUpdateRow.Visibility        = Visibility.Visible;
    }

    private void StartSettingsSpin()
    {
        var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
            { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
        _settingsSpin = new System.Windows.Media.Animation.Storyboard();
        System.Windows.Media.Animation.Storyboard.SetTarget(spin, SettingsUpdateSpin);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(spin, new PropertyPath(RotateTransform.AngleProperty));
        _settingsSpin.Children.Add(spin);
        _settingsSpin.Begin();
    }

    private void StopSettingsSpin()
    {
        _settingsSpin?.Stop();
        _settingsSpin = null;
        SettingsUpdateSpin.Angle = 0;
    }

    /// <summary>Hands over to the full-screen gate for the download and install.</summary>
    private void SettingsUpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsUpdate is not { State: UpdateState.Available } info) return;
        CloseSettings();
        _pendingUpdate = info;
        ShowUpdateGate();
        SetUpdateAvailable(info);
    }

    private void OpenSettings()
    {
        LoadSettingsIntoUi();
        SettingsOverlay.Visibility = Visibility.Visible;

        // Live mic meter while the page is open.
        _voice.StartMonitor();
        _meterTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _meterTimer.Tick -= MeterTick;
        _meterTimer.Tick += MeterTick;
        _meterTimer.Start();
    }

    private void CloseSettings()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        _meterTimer?.Stop();
        _voice.StopMonitor();
        if (_ptt.Capturing)
        {
            _ptt.Capturing = false;
            PttKeyBtn.Content = PushToTalkHook.KeyName(_settings.PttKey);
        }
    }

    private void MeterTick(object? sender, EventArgs e)
    {
        double level = _voice.InputLevel;
        // Fast up, slow down: the bar should jump on speech and settle, not flicker.
        _meterShown = level > _meterShown ? level : Math.Max(0, _meterShown - 0.045);
        if (MicLevelFill.Parent is FrameworkElement track)
            MicLevelFill.Width = Math.Max(0, track.ActualWidth * _meterShown);
    }

    private void LoadSettingsIntoUi()
    {
        _suppressSettingEvents = true;
        try
        {
            var s = _settings;
            StartupSwitch.IsChecked     = s.RunAtStartup;
            StartTraySwitch.IsChecked   = s.StartInTray;
            StartTraySwitch.IsEnabled   = s.RunAtStartup;
            CloseTraySwitch.IsChecked   = s.CloseToTray;
            WinNotifySwitch.IsChecked   = s.WindowsNotifications;
            SoundsSwitch.IsChecked      = s.Sounds;
            SoundJoinSwitch.IsChecked   = s.SoundJoinLeave;
            SoundMsgSwitch.IsChecked    = s.SoundMessage;
            SoundFileSwitch.IsChecked   = s.SoundFile;
            SoundJoinSwitch.IsEnabled = SoundMsgSwitch.IsEnabled = SoundFileSwitch.IsEnabled = s.Sounds;
            PttSwitch.IsChecked         = s.PushToTalk;
            PttKeyBtn.Content           = PushToTalkHook.KeyName(s.PttKey);
            ShareStatusSwitch.IsChecked = s.ShareStatus;
            AfkBox.Text                 = s.AfkMinutes.ToString();
            RenderLanguageButtons();
            FillDevices(InputDeviceBox,  VoiceManager.ListInputs(),  s.InputDeviceId);
            FillDevices(OutputDeviceBox, VoiceManager.ListOutputs(), s.OutputDeviceId);
        }
        finally { _suppressSettingEvents = false; }
    }

    private void FillDevices(ComboBox box, List<AudioDevice> devices, string? selectedId)
    {
        box.Items.Clear();
        box.Items.Add(new AudioDevice(null, Loc.T("Settings_DefaultDevice")));
        foreach (var d in devices) box.Items.Add(d);
        var match = box.Items.OfType<AudioDevice>().FirstOrDefault(d => d.Id == selectedId && selectedId != null);
        box.SelectedItem = match ?? box.Items[0];
        box.DisplayMemberPath = nameof(AudioDevice.Name);
    }

    private void RenderLanguageButtons()
    {
        bool fa = Loc.I.Language == AppLanguage.Persian;
        LangFaBtn.Style = (Style)FindResource(fa  ? "Btn.Primary" : "Btn.Ghost");
        LangEnBtn.Style = (Style)FindResource(!fa ? "Btn.Primary" : "Btn.Ghost");
        LangFaBtn.Height = LangEnBtn.Height = 34;
        LangFaBtn.MinHeight = LangEnBtn.MinHeight = 34;
        LangFaBtn.FontSize = LangEnBtn.FontSize = 12.5;
        LangFaBtn.Padding = LangEnBtn.Padding = new Thickness(14, 0, 14, 0);
    }

    private void LangPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string code }) return;
        Loc.I.Set(code == "en" ? AppLanguage.English : AppLanguage.Persian);
        RenderLanguageButtons();
    }

    private void Setting_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingEvents || sender is not CheckBox box) return;
        bool on = box.IsChecked == true;
        var s = _settings;

        switch (box.Name)
        {
            case nameof(StartupSwitch):
                if (on && !StartupRegistration.Enable())
                {
                    box.IsChecked = false;
                    Notify(Loc.T("Settings_StartupFailTitle"), Loc.T("Settings_StartupFailBody"), ToastKind.Error);
                    return;
                }
                if (!on) StartupRegistration.Disable();
                s.RunAtStartup = on;
                StartTraySwitch.IsEnabled = on;
                break;
            case nameof(StartTraySwitch):   s.StartInTray          = on; break;
            case nameof(CloseTraySwitch):   s.CloseToTray          = on; break;
            case nameof(WinNotifySwitch):   s.WindowsNotifications = on; break;
            case nameof(SoundsSwitch):
                s.Sounds = on;
                SoundJoinSwitch.IsEnabled = SoundMsgSwitch.IsEnabled = SoundFileSwitch.IsEnabled = on;
                break;
            case nameof(SoundJoinSwitch):   s.SoundJoinLeave = on; break;
            case nameof(SoundMsgSwitch):    s.SoundMessage   = on; break;
            case nameof(SoundFileSwitch):   s.SoundFile      = on; break;
            case nameof(PttSwitch):
                s.PushToTalk      = on;
                _ptt.Enabled      = on;
                _voice.PushToTalk = on;
                _voice.TalkKeyDown = false;
                SetMicVisual(_voice.IsMicActive);
                break;
            case nameof(ShareStatusSwitch):
                s.ShareStatus = on;
                // Turned off: leave everyone seeing a plain "online" rather than a stale "in game".
                var p = on ? _presence.Current : Presence.Online;
                if (_room.IsActive) _room.SetMyPresence(p);
                if (_vlan.IsActive) _vlan.SetMyPresence(p);
                break;
        }
        s.Save();
    }

    private void SoundTest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string which }) return;
        AppSounds.PlayAlways(which switch
        {
            "join"    => AppSound.Join,
            "message" => AppSound.Message,
            _         => AppSound.File
        });
    }

    private void AudioDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingEvents || sender is not ComboBox box) return;
        string? id = (box.SelectedItem as AudioDevice)?.Id;
        if (box == InputDeviceBox)  { _settings.InputDeviceId  = id; _voice.InputDeviceId  = id; }
        else                        { _settings.OutputDeviceId = id; _voice.OutputDeviceId = id; }
        _settings.Save();
        _voice.ApplyDevices();
    }

    private void PttKey_Click(object sender, RoutedEventArgs e)
    {
        _ptt.Capturing    = true;
        PttKeyBtn.Content = Loc.T("Settings_PressKey");
    }

    private void AfkBox_LostFocus(object sender, RoutedEventArgs e) => CommitAfk();
    private void AfkBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitAfk(); e.Handled = true; }
    }

    private void CommitAfk()
    {
        if (int.TryParse(AfkBox.Text, out int m) && m is >= 1 and <= 240)
        {
            _settings.AfkMinutes = m;
            _settings.Save();
        }
        AfkBox.Text = _settings.AfkMinutes.ToString();
    }

    // ── About: what's new, bug report, logs ──────────────────────────────────

    private void WhatsNew_Click(object sender, RoutedEventArgs e)
        => _ = ShowModal(Loc.T("Whatsnew_Title", AppVersion), Loc.T("Whatsnew_Body"), Loc.T("Common_Ok"), null, startAligned: true);

    /// <summary>First launch of a new version: say what changed, once.</summary>
    private void MaybeShowWhatsNew()
    {
        if (_settings.LastSeenVersion == AppVersion) return;
        _settings.LastSeenVersion = AppVersion;
        _settings.Save();
        _ = ShowModal(Loc.T("Whatsnew_Title", AppVersion), Loc.T("Whatsnew_Body"), Loc.T("Common_Ok"), null, startAligned: true);
    }

    /// <summary>Where bug reports go. One place to change when the address changes.</summary>
    private const string SupportEmail = "vlanplat.help@gmail.com";

    private async void Report_Click(object sender, RoutedEventArgs e)
    {
        // The logs go to the desktop quietly; the dialog is about the address. If the
        // bundle cannot be written the address is still worth showing.
        string? file = null;
        try
        {
            file = System.IO.Path.GetFileName(await Task.Run(() => AppLog.CreateReportZip(BuildDiagnostics())));
            AppLog.Info("report", $"bundle written: {file}");
        }
        catch (Exception ex) { AppLog.Error("report", "bundle failed", ex); }

        string body = Loc.T("Settings_ReportBody", SupportEmail)
                    + (file != null ? "\n\n" + Loc.T("Settings_ReportAttach", file) : "");

        bool copy = await ShowModal(Loc.T("Settings_Report"), body,
            Loc.T("Settings_CopyEmail"), Loc.T("Common_NeverMind"));
        if (!copy) return;
        try
        {
            Clipboard.SetText(SupportEmail);
            ShowToast(Loc.T("Common_Copied"), SupportEmail, ToastKind.Success);
        }
        catch { }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppLog.Dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppLog.Dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Virtual LAN Platform v{AppVersion}");
        sb.AppendLine($"time      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"os        : {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
        sb.AppendLine($"admin     : {IsRunningAsAdmin()}");
        sb.AppendLine($"language  : {Loc.I.Language}");
        sb.AppendLine($"wintun    : {(WintunAvailable() ? "ok" : "MISSING")}");
        sb.AppendLine($"fw udp    : {FirewallRuleExists("VirtualLANPlatform UDP")}");
        sb.AppendLine($"fw vlan   : {FirewallRuleExists("VirtualLANPlatform VLAN")}");
        sb.AppendLine($"fw subnet : {FirewallRuleExists("VirtualLANPlatform VLAN Subnet")}");
        sb.AppendLine();
        sb.AppendLine("[room]");
        sb.AppendLine($"active={_room.IsActive} host={_room.IsHost} peers={_p2p.PeerCount} members={_room.GetMembers().Count}");
        sb.AppendLine();
        sb.AppendLine("[vlan]");
        sb.AppendLine($"active={_vlan.IsActive} host={_vlan.IsHost} ip={_vlan.AssignedIp} public={_vlan.PublicEndpoint ?? "-"} lan={_vlan.LanEndpoint ?? "-"}");
        foreach (var m in _vlan.Members)
            sb.AppendLine($"  {m.Name} {m.Ip} host={m.IsHost} self={m.IsSelf} status={m.Status} rtt={m.RttMs}");
        sb.AppendLine();
        sb.AppendLine("[adapters]");
        try
        {
            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var ips = n.GetIPProperties().UnicastAddresses
                    .Where(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(u => $"{u.Address}/{u.PrefixLength}");
                sb.AppendLine($"  {n.Name} [{n.NetworkInterfaceType}] {n.OperationalStatus} {string.Join(",", ips)}");
            }
        }
        catch (Exception ex) { sb.AppendLine($"  (failed: {ex.Message})"); }
        sb.AppendLine();
        sb.AppendLine("[settings]");
        sb.AppendLine($"ptt={_settings.PushToTalk} key={PushToTalkHook.KeyName(_settings.PttKey)} in={_settings.InputDeviceId ?? "default"} out={_settings.OutputDeviceId ?? "default"}");
        sb.AppendLine($"startup={_settings.RunAtStartup} tray={_settings.CloseToTray} sounds={_settings.Sounds} notify={_settings.WindowsNotifications} status={_settings.ShareStatus}/{_settings.AfkMinutes}m");
        return sb.ToString();
    }

    // ── Image previews for file cards ────────────────────────────────────────

    private void FilePreview_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileNotification { LocalPath: { Length: > 0 } path } }) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }
}

/// <summary>One row of the virtual-LAN members list.</summary>
public sealed class VlanMemberItem : INotifyPropertyChanged
{
    private string   _name = "";
    private Presence _status;
    private int      _rtt;

    public string Name   { get => _name; set { _name = value; Notify(); Notify(nameof(SubLabel)); } }
    public string Ip     { get; init; } = "";
    public bool   IsHost { get; init; }
    public bool   IsSelf { get; init; }

    public Presence Status
    {
        get => _status;
        set { _status = value; Notify(); Notify(nameof(StatusBrush)); Notify(nameof(StatusLabel)); Notify(nameof(SubLabel)); Notify(nameof(SubVisibility)); }
    }

    public int RttMs
    {
        get => _rtt;
        set { _rtt = value; Notify(); Notify(nameof(PingText)); Notify(nameof(PingBrush)); Notify(nameof(PingBg)); }
    }

    public Brush  StatusBrush => TestWindow.PresenceBrush(Status);
    public string StatusLabel => TestWindow.PresenceLabel(Status);

    public string SubLabel
    {
        get
        {
            var parts = new List<string>(3);
            if (IsSelf) parts.Add(Loc.T("Member_You"));
            if (IsHost) parts.Add(Loc.T("Member_HostBadge"));
            if (Status != Presence.Online) parts.Add(StatusLabel);
            return string.Join(" · ", parts);
        }
    }
    public Visibility SubVisibility => SubLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string PingText => IsSelf ? "—" : $"{RttMs} ms";
    public Brush  PingBrush => IsSelf ? Gray : RttMs switch
    {
        < 60  => Green,
        < 150 => Amber,
        _     => Red
    };
    public Brush PingBg => IsSelf ? Dim : RttMs switch
    {
        < 60  => GreenBg,
        < 150 => AmberBg,
        _     => RedBg
    };

    private static readonly Brush Green   = Frozen(0x22, 0xC5, 0x5E);
    private static readonly Brush Amber   = Frozen(0xF5, 0x9E, 0x0B);
    private static readonly Brush Red     = Frozen(0xEF, 0x44, 0x44);
    private static readonly Brush Gray    = Frozen(0x5B, 0x63, 0x77);
    private static readonly Brush GreenBg = Frozen(0x12, 0x2A, 0x1F);
    private static readonly Brush AmberBg = Frozen(0x2E, 0x24, 0x10);
    private static readonly Brush RedBg   = Frozen(0x2E, 0x14, 0x16);
    private static readonly Brush Dim     = Frozen(0x1A, 0x20, 0x30);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
