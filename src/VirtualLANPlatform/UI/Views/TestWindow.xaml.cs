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
using VirtualLANPlatform.UI.Localization;
using VirtualLANPlatform.Core.Protocol;
using VirtualLANPlatform.Core.Room;
using VirtualLANPlatform.Core.ScreenShare;
using VirtualLANPlatform.Core.Services;
using VirtualLANPlatform.Core.Storage;
using VirtualLANPlatform.Core.Voice;
using VirtualLANPlatform.UI.Emoji;

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
    private readonly VirtualLANPlatform.Core.VirtualLan.VirtualLanManager _vlan = new();
    private bool                        _vlanBusy;
    private string?                     _remoteSharerUsername;
    private Storyboard?                 _dotPulse;
    private WaveOutEvent?               _audioOut;
    private BufferedWaveProvider?       _audioBuffer;
    private string                      _activeTab = "chat";

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

        // Catch hyperlink navigations bubbled from any RichTextBox in ChatList
        AddHandler(System.Windows.Documents.Hyperlink.RequestNavigateEvent,
            new System.Windows.Navigation.RequestNavigateEventHandler(OnLinkNavigate));

        WireVirtualLan();

        Loc.I.LanguageChanged += OnLanguageChanged;

        Loaded += (_, _) =>
        {
            FadeInWindow();
            // Gate the lobby behind an update check on every launch.
            _ = RunUpdateGate(startup: true);
        };
    }

    /// <summary>
    /// Fades the window in on open, then *detaches* the animation.
    /// An animation clock left attached to Window.Opacity (FillBehavior.HoldEnd,
    /// which is the default) keeps the window on the layered-window render path
    /// forever, and ClearType is off on that path — every glyph in the app renders
    /// with grayscale antialiasing and looks blurry. Clearing the animation and
    /// writing Opacity as a plain local value puts the window back on the normal
    /// path, so text is subpixel-sharp once the fade finishes.
    /// </summary>
    private void FadeInWindow()
    {
        var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(0.28)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior   = FillBehavior.Stop
        };
        fade.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    // ── Virtual LAN (standalone) ─────────────────────────────────────────────

    private void WireVirtualLan()
    {
        _vlan.Connected += ip => Dispatch(() =>
        {
            RenderVlanState();
            VlanStatusText.Text = Loc.T("Vlan_ConnectedStatus", ip);

            if (!_vlan.IsHost)
            {
                Notify(Loc.T("Vlan_JoinedTitle"), Loc.T("Vlan_JoinedBody", ip), ToastKind.Success);
            }
            else if (_vlan.PublicEndpoint is { Length: > 0 })
            {
                Notify(Loc.T("Vlan_OnTitle"), Loc.T("Vlan_OnBody", ip), ToastKind.Success);
            }
            else
            {
                // The port could not be opened, so only the local network can reach us.
                // Say so now rather than letting a friend fail to connect and guess why.
                Notify(Loc.T("Vlan_OnLocalTitle"), Loc.T("Vlan_OnLocalBody"), ToastKind.Warn);
            }
        });

        _vlan.Disconnected += () => Dispatch(() =>
        {
            RenderVlanState();
            Notify(Loc.T("Vlan_OffTitle"), Loc.T("Vlan_OffBody"));
        });

        _vlan.StatusChanged += s  => Dispatch(() => { if (_vlan.IsActive) VlanStatusText.Text = s; });
        _vlan.Error        += msg => Dispatch(() =>
        {
            Notify(Loc.T("Vlan_ErrTitle"), msg, ToastKind.Error, alsoTray: true);
            RenderVlanState();
        });
        RenderVlanState();
    }

    private void RenderVlanState()
    {
        bool on = _vlan.IsActive;
        VlanIdlePanel.Visibility    = on ? Visibility.Collapsed : Visibility.Visible;
        VlanActivePanel.Visibility  = on ? Visibility.Visible   : Visibility.Collapsed;
        VlanHostBtn.IsEnabled       = !_vlanBusy;
        VlanJoinBtn.IsEnabled       = !_vlanBusy;
        VlanJoinIpBox.IsEnabled     = !_vlanBusy;
        VlanDisconnectBtn.IsEnabled = !_vlanBusy;

        if (!on) return;

        VlanIpText.Text = _vlan.AssignedIp;
        // Host and guest are meaningfully different situations: the host is the one
        // whose real IP everyone else needs, the guest is already reachable.
        VlanRoleText.Text = Loc.T(_vlan.IsHost ? "Vlan_RoleHost" : "Vlan_RoleGuest");

        // Only the host has an address worth handing out; a guest is already inside.
        VlanInviteBtn.Visibility = _vlan.IsHost && _vlan.PublicEndpoint is { Length: > 0 }
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void VlanHost_Click(object sender, RoutedEventArgs e)
    {
        if (_vlanBusy || _vlan.IsActive) return;
        await RunPreflight();
    }

    // ── Pre-flight checks ─────────────────────────────────────────────────────

    private enum CheckState { Running, Ok, Warn, Fail }

    /// <summary>
    /// One row of the pre-flight list. Holds its own visuals so a check can flip from
    /// spinner to verdict without rebuilding the list.
    /// </summary>
    private sealed class CheckRow
    {
        public required Border            Chip   { get; init; }
        public required PackIconLucide    Icon   { get; init; }
        public required TextBlock         Detail { get; init; }
    }

    private UpnpProbe? _lastProbe;

    /// <summary>
    /// Walks every condition the virtual LAN depends on, showing each verdict as it
    /// lands. The point is that all of these fail silently at runtime — an unaddressed
    /// adapter, a firewalled subnet, a port nobody forwarded — and look identical from
    /// the outside: a network that is "on" and carries nothing.
    /// </summary>
    private async Task RunPreflight()
    {
        _vlanBusy = true; RenderVlanState();

        PreflightList.Children.Clear();
        PreflightSubtitle.Text        = Loc.T("Common_OneMoment");
        PreflightSummary.Visibility   = Visibility.Collapsed;
        PreflightButtons.Visibility   = Visibility.Collapsed;
        PreflightHelpBtn.Visibility   = Visibility.Collapsed;
        VlanPreflight.Visibility      = Visibility.Visible;

        var admin    = AddCheck(Loc.T("Pf_Admin"), Loc.T("Pf_AdminWhy"));
        var driver   = AddCheck(Loc.T("Pf_Driver"), Loc.T("Pf_DriverWhy"));
        var firewall = AddCheck(Loc.T("Pf_Firewall"), Loc.T("Pf_FirewallWhy"));
        var upnp     = AddCheck(Loc.T("Pf_Upnp"), Loc.T("Pf_UpnpWhy"));
        var reach    = AddCheck(Loc.T("Pf_Reach"), Loc.T("Pf_ReachWhy"));

        // 1. Administrator — Wintun cannot create an adapter without it.
        await Task.Delay(220);
        bool isAdmin = IsRunningAsAdmin();
        Set(admin, isAdmin ? CheckState.Ok : CheckState.Fail,
            Loc.T(isAdmin ? "Pf_AdminOk" : "Pf_AdminFail"));

        // 2. The driver DLL actually loads.
        await Task.Delay(220);
        bool hasDriver = WintunAvailable();
        Set(driver, hasDriver ? CheckState.Ok : CheckState.Fail,
            Loc.T(hasDriver ? "Pf_DriverOk" : "Pf_DriverFail"));

        // 3. Our firewall rules. Missing is recoverable — they are re-added on connect.
        await Task.Delay(220);
        bool fwOk = FirewallRuleExists("VirtualLANPlatform VLAN");
        Set(firewall, fwOk ? CheckState.Ok : CheckState.Warn,
            Loc.T(fwOk ? "Pf_FirewallOk" : "Pf_FirewallWarn"));

        // 4 + 5. One network round trip answers both.
        PreflightSubtitle.Text = Loc.T("Pf_CheckingRouter");
        UpnpProbe probe;
        try { probe = await _portProbe.ProbeAsync(); }
        catch { probe = new UpnpProbe(UpnpState.NotFound); }
        _lastProbe = probe;

        switch (probe.State)
        {
            case UpnpState.Available:
                Set(upnp,  CheckState.Ok, probe.RouterName is { Length: > 0 } r ? r : Loc.T("Pf_UpnpOn"));
                Set(reach, CheckState.Ok, Loc.T("Pf_ReachOk", probe.ExternalIp));
                break;

            case UpnpState.CarrierNat:
                // The router is fine; the ISP is the blocker, so do not blame the router.
                Set(upnp,  CheckState.Ok,   probe.RouterName is { Length: > 0 } r2 ? r2 : Loc.T("Pf_UpnpOn"));
                Set(reach, CheckState.Fail, Loc.T("Pf_ReachCgnat", probe.ExternalIp));
                break;

            case UpnpState.NoService:
                Set(upnp,  CheckState.Warn, Loc.T("Pf_UpnpNoService"));
                Set(reach, CheckState.Warn, Loc.T("Pf_ReachManual"));
                break;

            default:
                Set(upnp,  CheckState.Fail, Loc.T("Pf_UpnpOff"));
                Set(reach, CheckState.Warn, Loc.T("Pf_ReachUnknown"));
                break;
        }

        _vlanBusy = false; RenderVlanState();
        ShowPreflightVerdict(isAdmin && hasDriver, probe);
    }

    private void ShowPreflightVerdict(bool canRunAtAll, UpnpProbe probe)
    {
        PreflightButtons.Visibility = Visibility.Visible;

        if (!canRunAtAll)
        {
            PreflightSubtitle.Text      = Loc.T("Pf_BlockedTitle");
            PreflightSummaryText.Text   = Loc.T("Pf_BlockedBody");
            PreflightSummaryText.Foreground = Res("Danger");
            PreflightSummary.Background = Brush("#1AEF4444");
            PreflightSummary.Visibility = Visibility.Visible;
            PreflightGoBtn.Visibility   = Visibility.Collapsed;
            return;
        }

        PreflightGoBtn.Visibility = Visibility.Visible;

        if (probe.State == UpnpState.Available)
        {
            PreflightSubtitle.Text          = Loc.T("Pf_ReadyTitle");
            PreflightSummaryText.Text       = Loc.T("Pf_ReadyBody");
            PreflightSummaryText.Foreground = Res("Ok");
            PreflightSummary.Background     = Brush("#1A22C55E");
            PreflightGoText.Text            = Loc.T("Preflight_Go");
        }
        else
        {
            PreflightSubtitle.Text = Loc.T("Pf_LocalTitle");
            PreflightSummaryText.Text = Loc.T(probe.State == UpnpState.CarrierNat
                ? "Pf_LocalCgnat" : "Pf_LocalNoPort");
            PreflightSummaryText.Foreground = Res("Warn");
            PreflightSummary.Background     = Brush("#1AF59E0B");
            PreflightGoText.Text            = Loc.T("Pf_GoAnyway");
            PreflightHelpBtn.Visibility     = Visibility.Visible;
        }

        PreflightSummary.Visibility = Visibility.Visible;
    }

    /// <summary>Adds a row in the Running state and returns a handle for updating it.</summary>
    private CheckRow AddCheck(string title, string detail)
    {
        var icon = new PackIconLucide
        {
            Kind                = PackIconLucideKind.Circle,
            Width               = 15,
            Height              = 15,
            Foreground          = Res("TxtLo"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment   = System.Windows.VerticalAlignment.Center
        };

        var chip = new Border
        {
            Width             = 30,
            Height            = 30,
            CornerRadius      = new CornerRadius(10),
            Background        = Brush("#1A1F2E"),
            Margin            = new Thickness(0, 0, 13, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child             = icon
        };

        var detailText = new TextBlock
        {
            Text         = detail,
            FontSize     = 11.5,
            Foreground   = Res("TxtLo"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 2, 0, 0)
        };

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text       = title,
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("TxtHi")
        });
        texts.Children.Add(detailText);

        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 13)
        };
        row.Children.Add(chip);
        row.Children.Add(texts);
        PreflightList.Children.Add(row);

        return new CheckRow { Chip = chip, Icon = icon, Detail = detailText };
    }

    private void Set(CheckRow row, CheckState state, string detail)
    {
        var (kind, colorKey, chipHex) = state switch
        {
            CheckState.Ok   => (PackIconLucideKind.Check,         "Ok",     "#1A22C55E"),
            CheckState.Warn => (PackIconLucideKind.TriangleAlert, "Warn",   "#1AF59E0B"),
            CheckState.Fail => (PackIconLucideKind.X,             "Danger", "#1AEF4444"),
            _               => (PackIconLucideKind.Circle,        "TxtLo",  "#1A1F2E")
        };

        row.Icon.Kind        = kind;
        row.Icon.Foreground  = Res(colorKey);
        row.Chip.Background  = Brush(chipHex);
        row.Detail.Text      = detail;
        row.Detail.Foreground = state == CheckState.Ok ? Res("TxtMid") : Res(colorKey);
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Proves wintun.dll resolves and its exports bind. The returned version is ignored:
    /// it is legitimately 0 until an adapter exists, so only the absence of a load
    /// failure is meaningful here.
    /// </summary>
    private static bool WintunAvailable()
    {
        try { Core.VirtualLan.Wintun.WintunGetRunningDriverVersion(); return true; }
        catch { return false; }
    }

    private static bool FirewallRuleExists(string name)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "netsh", $"advfirewall firewall show rule name=\"{name}\"")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            })!;
            return p.WaitForExit(4000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private async void PreflightGo_Click(object sender, RoutedEventArgs e)
    {
        VlanPreflight.Visibility = Visibility.Collapsed;
        await StartVlanHost();
    }

    private void PreflightHelp_Click(object sender, RoutedEventArgs e)
    {
        VlanPreflight.Visibility = Visibility.Collapsed;
        if (_lastProbe != null) ShowUpnpHelp(_lastProbe);
    }

    private void PreflightClose_Click(object sender, RoutedEventArgs e)
        => VlanPreflight.Visibility = Visibility.Collapsed;

    private async Task StartVlanHost()
    {
        _vlanBusy = true; RenderVlanState();
        try
        {
            // The VLAN's own firewall rule (port 42778) is opened unconditionally at
            // app startup — no need to touch the chat room's port/firewall state here.
            string username = UsernameBox.Text.Trim() is { Length: > 0 } u ? u : Loc.T("Vlan_DefaultHostName");
            bool ok = await _vlan.HostAsync(username, GetSelectedAdapterIP());
            if (!ok)
                Notify(Loc.T("Vlan_FailedTitle"), Loc.T("Vlan_FailedBody"), ToastKind.Error);
        }
        finally { _vlanBusy = false; RenderVlanState(); }
    }

    // ── UPnP guidance ─────────────────────────────────────────────────────────

    private readonly PortMapper _portProbe = new();

    /// <summary>
    /// Explains, in the user's own terms and with their own router's addresses, why
    /// hosting will not be reachable and what to do about it.
    /// </summary>
    private void ShowUpnpHelp(UpnpProbe probe)
    {
        string gateway = probe.GatewayIp is { Length: > 0 } g ? g : "192.168.1.1";
        UpnpSteps.Children.Clear();

        switch (probe.State)
        {
            case UpnpState.CarrierNat:
                // A mapping would be accepted here and still lead nowhere, so the router
                // is not the thing to go fix — the ISP is.
                UpnpIcon.Kind          = PackIconLucideKind.CircleAlert;
                UpnpIconChip.Background = Brush("#26EF4444");
                UpnpIcon.Foreground     = Res("Danger");
                UpnpTitle.Text          = Loc.T("Help_CgnatTitle");
                UpnpSubtitle.Text       = probe.RouterName ?? "";
                UpnpExplain.Text = Loc.T("Help_CgnatBody", probe.ExternalIp);
                AddStep(1, Loc.T("Help_CgnatStep1"), Loc.T("Help_CgnatStep1B"));
                AddStep(2, Loc.T("Help_CgnatStep2"), Loc.T("Help_CgnatStep2B"));
                AddStep(3, Loc.T("Help_CgnatStep3"), Loc.T("Help_CgnatStep3B"));
                UpnpAnywayBtn.Content = Loc.T("Preflight_Anyway");
                break;

            case UpnpState.NoService:
                UpnpIcon.Kind           = PackIconLucideKind.TriangleAlert;
                UpnpIconChip.Background = Brush("#26F59E0B");
                UpnpIcon.Foreground     = Res("Warn");
                UpnpTitle.Text          = Loc.T("Help_NoSvcTitle");
                UpnpSubtitle.Text       = probe.RouterName ?? "";
                UpnpExplain.Text = Loc.T("Help_NoSvcBody");
                AddManualForwardSteps(gateway, probe.LocalIp);
                break;

            default: // NotFound
                UpnpIcon.Kind           = PackIconLucideKind.WifiOff;
                UpnpIconChip.Background = Brush("#26F59E0B");
                UpnpIcon.Foreground     = Res("Warn");
                UpnpTitle.Text          = Loc.T("Help_OffTitle");
                UpnpSubtitle.Text       = Loc.T("Help_OffSubtitle");
                UpnpExplain.Text = Loc.T("Help_OffBody");
                AddStep(1, Loc.T("Help_OffStep1"), gateway, isCode: true);
                AddStep(2, Loc.T("Help_OffStep2"), Loc.T("Help_OffStep2B"));
                AddStep(3, Loc.T("Help_OffStep3"), Loc.T("Help_OffStep3B"));
                AddStep(4, Loc.T("Help_OffStep4"), Loc.T("Help_OffStep4B"));
                AddStep(5, Loc.T("Help_OffStep5"), Loc.T("Help_OffStep5B"));
                AddDivider(Loc.T("Help_ManualDivider"));
                AddManualForwardSteps(gateway, probe.LocalIp, startAt: 6);
                break;
        }

        AddDivider(Loc.T("Help_EasiestDivider"));
        AddStep(0, Loc.T("Help_EasiestTitle"), Loc.T("Help_EasiestBody"));

        UpnpHelp.Visibility = Visibility.Visible;
    }

    private void AddManualForwardSteps(string gateway, string? localIp, int startAt = 1)
    {
        AddStep(startAt, Loc.T("Help_FwdStep1"), Loc.T("Help_FwdStep1B", gateway));
        AddStep(startAt + 1, Loc.T("Help_FwdStep2"),
                   $"Protocol: UDP\nExternal Port: {Core.VirtualLan.VirtualLanManager.VlanPort}\n" +
                   $"Internal Port: {Core.VirtualLan.VirtualLanManager.VlanPort}\n" +
                   $"Internal IP: {localIp ?? Loc.T("Help_FwdInternalIp")}",
                   isCode: true);
        AddStep(startAt + 2, Loc.T("Help_FwdStep3"), Loc.T("Help_FwdStep3B"));
    }

    /// <summary>One numbered step. <paramref name="number"/> 0 renders a bullet instead.</summary>
    private void AddStep(int number, string title, string detail, bool isCode = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width             = 26,
            Height            = 26,
            CornerRadius      = new CornerRadius(9),
            Background        = Brush(number == 0 ? "#1D2436" : "#262F4D"),
            Margin            = new Thickness(0, 1, 11, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text                = number == 0 ? "★" : number.ToString(),
                FontSize            = number == 0 ? 12 : 12.5,
                FontWeight          = FontWeights.Bold,
                Foreground          = Res("Accent2"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment   = System.Windows.VerticalAlignment.Center
            }
        };
        Grid.SetColumn(badge, 0);

        var texts = new StackPanel();
        texts.Children.Add(new TextBlock
        {
            Text         = title,
            FontSize     = 13,
            FontWeight   = FontWeights.SemiBold,
            Foreground   = Res("TxtHi"),
            TextWrapping = TextWrapping.Wrap
        });
        texts.Children.Add(isCode
            ? new Border
            {
                Background      = Brush("#12161F"),
                BorderBrush     = Res("Stroke"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(11, 7, 11, 7),
                Margin          = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text          = detail,
                    FontFamily    = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize      = 12.5,
                    Foreground    = Res("Accent2"),
                    FlowDirection = System.Windows.FlowDirection.LeftToRight,
                    LineHeight    = 19,
                    TextWrapping  = TextWrapping.Wrap
                }
            }
            : new TextBlock
            {
                Text         = detail,
                FontSize     = 12,
                Foreground   = Res("TxtMid"),
                LineHeight   = 20,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 3, 0, 0)
            });
        Grid.SetColumn(texts, 1);

        grid.Children.Add(badge);
        grid.Children.Add(texts);
        UpnpSteps.Children.Add(grid);
    }

    private void AddDivider(string label)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left  = new Border { Height = 1, Background = Res("StrokeSoft"), VerticalAlignment = VerticalAlignment.Center };
        var text  = new TextBlock
        {
            Text       = label,
            FontSize   = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("TxtLo"),
            Margin     = new Thickness(12, 0, 12, 0)
        };
        var right = new Border { Height = 1, Background = Res("StrokeSoft"), VerticalAlignment = VerticalAlignment.Center };

        Grid.SetColumn(left, 0); Grid.SetColumn(text, 1); Grid.SetColumn(right, 2);
        grid.Children.Add(left); grid.Children.Add(text); grid.Children.Add(right);
        UpnpSteps.Children.Add(grid);
    }

    private static System.Windows.Media.SolidColorBrush Brush(string hex)
        => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    private async void UpnpRetry_Click(object sender, RoutedEventArgs e)
    {
        UpnpHelp.Visibility = Visibility.Collapsed;
        VlanHost_Click(sender, e);
        await Task.CompletedTask;
    }

    private async void UpnpAnyway_Click(object sender, RoutedEventArgs e)
    {
        UpnpHelp.Visibility = Visibility.Collapsed;
        await StartVlanHost();
    }

    private void UpnpClose_Click(object sender, RoutedEventArgs e)
        => UpnpHelp.Visibility = Visibility.Collapsed;

    private void VlanJoinIpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        VlanJoin_Click(sender, e);
        e.Handled = true;
    }

    private async void VlanJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_vlanBusy || _vlan.IsActive) return;

        string hostIp = VlanJoinIpBox.Text.Trim();
        if (hostIp.Length == 0)
        {
            Notify(Loc.T("Vlan_NoIpTitle"), Loc.T("Vlan_NoIpBody"), ToastKind.Warn);
            return;
        }

        _vlanBusy = true; RenderVlanState();
        try
        {
            string username = UsernameBox.Text.Trim() is { Length: > 0 } u ? u : Loc.T("Vlan_DefaultGuestName");
            if (!await _vlan.JoinAsync(hostIp, username))
                Notify(Loc.T("Vlan_JoinFailTitle"), Loc.T("Vlan_JoinFailBody"), ToastKind.Error);
        }
        finally { _vlanBusy = false; RenderVlanState(); }
    }

    private async void VlanDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_vlanBusy) return;
        _vlanBusy = true; RenderVlanState();
        // DisconnectAsync joins the tunnel read thread and shells netsh — off the UI thread.
        try { await Task.Run(() => _vlan.DisconnectAsync()); }
        finally { _vlanBusy = false; RenderVlanState(); }
    }

    private void VlanInviteCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_vlan.PublicEndpoint is not { Length: > 0 } pub) return;
        try
        {
            Clipboard.SetText(pub);
            ShowToast(Loc.T("Vlan_InviteCopied"), Loc.T("Vlan_InviteCopiedBody", pub), ToastKind.Success);
        }
        catch { }
    }

    private async void VlanCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_vlan.AssignedIp);
            ShowToast(Loc.T("Common_Copied"), Loc.T("Vlan_IpCopiedBody", _vlan.AssignedIp), ToastKind.Success);
            VlanCopied.Opacity = 1;
            await Task.Delay(1500);
            VlanCopied.Opacity = 0;
        }
        catch { }
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
        // Set here rather than bound in XAML: a live binding on an editable box would
        // overwrite the name the user typed the next time the language changed.
        UsernameBox.Text = Load(UsernamePath) is { Length: > 0 } saved
            ? saved
            : Loc.T("Lobby_DefaultName");
    }

    private void LoadSavedPort()
    {
        if (Load(PortPath) is { } saved && ushort.TryParse(saved, out ushort p) && p >= 1000)
            PortBox.Text = saved;
    }

    private void NetSettingsToggle_Click(object sender, MouseButtonEventArgs e)
    {
        bool opening = NetSettingsPanel.Visibility != Visibility.Visible;
        NetSettingsPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;

        var anim = new DoubleAnimation(opening ? 180 : 0, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        ChevronRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
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

    private void PortBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    private void PortBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(System.Windows.DataFormats.Text))
        {
            string text = e.DataObject.GetData(System.Windows.DataFormats.Text) as string ?? "";
            if (!text.All(char.IsDigit)) e.CancelCommand();
        }
        else e.CancelCommand();
    }

    private void PortBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!ushort.TryParse(PortBox.Text.Trim(), out ushort p) || p < 1000)
        {
            ShowToast(Loc.T("Port_BadTitle"), Loc.T("Port_BadBody"), ToastKind.Warn);
            PortBox.Text = "42777";
            Save(PortPath, "42777");
        }
        else Save(PortPath, p.ToString());
    }

    private void PortBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        PortBox_LostFocus(sender, e);
        e.Handled = true;
    }

    // ── Join IP field ────────────────────────────────────────────────────────

    private void JoinCodeBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(c => char.IsDigit(c) || c is '.' or ':');

    private void JoinCodeBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(System.Windows.DataFormats.Text))
        {
            string text = (e.DataObject.GetData(System.Windows.DataFormats.Text) as string ?? "").Trim();
            bool ok = text.StartsWith("vlan://", StringComparison.OrdinalIgnoreCase)
                   || text.All(c => char.IsDigit(c) || c is '.' or ':');
            if (!ok) e.CancelCommand();
        }
        else e.CancelCommand();
    }

    private void JoinCodeBox_LostFocus(object sender, RoutedEventArgs e)
        => JoinCodeBox.Text = JoinCodeBox.Text.Trim();

    private void JoinCodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        JoinCodeBox_LostFocus(sender, e);
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
            System.Drawing.Icon? icon = null;

            // Try the embedded logo.ico resource
            try
            {
                var rs = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/Assets/logo.ico"));
                if (rs?.Stream != null)
                    icon = new System.Drawing.Icon(rs.Stream);
            }
            catch { }

            // Fallback: read the icon embedded in the EXE (set by the project's <ApplicationIcon>)
            if (icon == null)
            {
                try
                {
                    string? exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (exe != null)
                        icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                }
                catch { }
            }

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon    = icon ?? System.Drawing.SystemIcons.Application,
                Visible = true,
                Text    = "Virtual LAN Platform"
            };
        }
        catch { }
    }

    // ── Notifications ─────────────────────────────────────────────────────────

    private enum ToastKind { Info, Success, Warn, Error }

    /// <summary>
    /// True when a member record refers to the local user. Join/leave events fan out to
    /// everyone including the sender, and announcing "you joined" to yourself is noise.
    /// </summary>
    private bool IsSelf(string username)
        => string.Equals(username.Trim(), UsernameBox.Text.Trim(), StringComparison.Ordinal);

    private void ShowNotification(string title, string body)
    {
        try { _trayIcon?.ShowBalloonTip(4000, title, body, System.Windows.Forms.ToolTipIcon.Info); }
        catch { }
    }

    /// <summary>
    /// The single entry point for telling the user something happened: an in-app toast
    /// always, plus a tray balloon when <paramref name="alsoTray"/> is set and the window
    /// isn't in front — so events that arrive while the user is in a game or another app
    /// still land somewhere they'll see them.
    /// </summary>
    private void Notify(string title, string message,
                        ToastKind kind = ToastKind.Info, bool alsoTray = false)
    {
        ShowToast(title, message, kind);
        if (alsoTray && (!IsActive || WindowState == WindowState.Minimized))
            ShowNotification(title, message);
    }

    private async void ShowToast(string title, string message, ToastKind kind = ToastKind.Info)
    {
        // Same headline already on screen? Don't stack a duplicate on top of it.
        if (ToastPanel.Children.OfType<Border>().Any(b => b.Tag is string t && t == title))
            return;

        var (accent, glyph) = kind switch
        {
            ToastKind.Success => (System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E),
                                  PackIconLucideKind.CircleCheck),
            ToastKind.Warn    => (System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B),
                                  PackIconLucideKind.TriangleAlert),
            ToastKind.Error   => (System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44),
                                  PackIconLucideKind.CircleAlert),
            _                 => (System.Windows.Media.Color.FromRgb(0x63, 0x66, 0xF1),
                                  PackIconLucideKind.Info)
        };
        var accentBrush = new System.Windows.Media.SolidColorBrush(accent);

        // Icon chip — the colour cue, instead of the old hairline edge stripe.
        var chip = new Border
        {
            Width               = 32,
            Height              = 32,
            CornerRadius        = new CornerRadius(10),
            Background          = new System.Windows.Media.SolidColorBrush(
                                      System.Windows.Media.Color.FromArgb(0x2E, accent.R, accent.G, accent.B)),
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(0, 0, 11, 0),
            Child = new PackIconLucide
            {
                Kind                = glyph,
                Width               = 17,
                Height              = 17,
                Foreground          = accentBrush,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment   = System.Windows.VerticalAlignment.Center
            }
        };

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text       = title,
            Foreground = Res("TxtHi"),
            FontWeight = FontWeights.SemiBold,
            FontSize   = 13
        });
        if (message.Length > 0)
            texts.Children.Add(new TextBlock
            {
                Text         = message,
                Foreground   = Res("TxtMid"),
                FontSize     = 12,
                LineHeight   = 18,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 2, 0, 0)
            });

        var closeBtn = new Button
        {
            Style             = (Style)FindResource("Btn.IconSm"),
            Width             = 24,
            Height            = 24,
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = new Thickness(8, -2, -4, 0),
            Content           = new PackIconLucide { Kind = PackIconLucideKind.X, Width = 12, Height = 12 }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(chip, 0);
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(closeBtn, 2);
        grid.Children.Add(chip);
        grid.Children.Add(texts);
        grid.Children.Add(closeBtn);

        var toast = new Border
        {
            Tag             = title,
            Background      = new System.Windows.Media.SolidColorBrush(
                                  System.Windows.Media.Color.FromRgb(0x19, 0x1F, 0x2C)),
            CornerRadius    = new CornerRadius(14),
            BorderBrush     = new System.Windows.Media.SolidColorBrush(
                                  System.Windows.Media.Color.FromRgb(0x2A, 0x31, 0x45)),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(13, 12, 13, 12),
            Margin          = new Thickness(0, 7, 0, 0),
            Opacity         = 0,
            Effect          = (System.Windows.Media.Effects.Effect)FindResource("SoftShadow"),
            Child           = grid
        };

        // Rise on a transform, not on Margin — the stack spacing is a layout value.
        var slide = new System.Windows.Media.TranslateTransform(0, 14);
        toast.RenderTransform = slide;

        // Newest at the bottom: the stack grows upward, so the freshest toast is the
        // one closest to where the eye already is.
        ToastPanel.Children.Add(toast);

        bool closed = false;
        async void Close()
        {
            if (closed) return;
            closed = true;
            toast.BeginAnimation(OpacityProperty,
                new DoubleAnimation(toast.Opacity, 0, new Duration(TimeSpan.FromSeconds(0.22))));
            await Task.Delay(240);
            ToastPanel.Children.Remove(toast);
        }
        closeBtn.Click += (_, _) => Close();

        // Detach on completion for the same reason the window fade does: a held opacity
        // clock keeps the element on a composited layer, where ClearType is off and the
        // toast text renders soft.
        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(0.2)))
                     { FillBehavior = FillBehavior.Stop };
        fadeIn.Completed += (_, _) =>
        {
            if (closed) return;
            toast.BeginAnimation(OpacityProperty, null);
            toast.Opacity = 1;
        };
        toast.BeginAnimation(OpacityProperty, fadeIn);

        slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, new Duration(TimeSpan.FromSeconds(0.26)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        await Task.Delay(4500);
        Close();
    }

    private Task<bool> ShowModal(string title, string message,
        string? confirmText = null, string? cancelText = null, bool isDanger = false)
    {
        // Resolved here rather than as parameter defaults: a default is baked in at
        // compile time and would keep whichever language the app started in.
        confirmText ??= Loc.T("Common_Confirm");
        cancelText  ??= Loc.T("Common_Cancel");

        ModalTitle.Text   = title;
        ModalMessage.Text = message;
        ModalButtons.Children.Clear();

        _modalTcs?.TrySetResult(false);
        _modalTcs = new TaskCompletionSource<bool>();

        var confirmBtn = new Button
        {
            Content = confirmText,
            Style   = (Style)(isDanger ? FindResource("Btn.Danger") : FindResource("Btn.Primary")),
            // Pinned so confirm and cancel line up — the two styles carry different MinHeights.
            Height  = 44,
            Padding = new Thickness(26, 0, 26, 0),
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
                Height  = 44,
                Padding = new Thickness(26, 0, 26, 0),
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

        _isRoomHost             = isHost;
        LeaveCloseText.Text     = Loc.T(isHost ? "Room_Close" : "Room_Leave");
        LeaveCloseBtn.IsEnabled = true;

        HostIpLabel.Text       = isHost ? ipPort : "";
        HostIpPanel.Visibility = isHost ? Visibility.Visible : Visibility.Collapsed;

        LobbyTopBar.Visibility = Visibility.Collapsed;
        VoiceStrip.Visibility     = Visibility.Visible;
        ChatPanel.Visibility      = Visibility.Visible;
        RightSidebar.Visibility   = Visibility.Visible;

        MicSlider.IsEnabled     = true;
        SpeakerSlider.IsEnabled = true;

        ChatInput.Focus();
        SetStatus(isHost ? "St_HostWaiting" : "St_Connected", StatusKind.Ok);
    }

    private void LeaveRoom()
    {
        LobbyActions.Visibility     = Visibility.Visible;
        LobbyHeaderPanel.Visibility = Visibility.Visible;
        RoomHeaderPanel.Visibility  = Visibility.Collapsed;
        Footer.Visibility           = Visibility.Visible;

        LobbyTopBar.Visibility = Visibility.Visible;
        VoiceStrip.Visibility  = Visibility.Collapsed;
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
        CloseScreenShareTab();

        SetMicVisual(true);
        SetSpeakerVisual(true);

        MemberList.Items.Clear();
        ChatList.Items.Clear();
        FileList.Items.Clear();
        _fileNotifs.Clear();
        _messages.Clear();
        _mutedMembers.Clear();
        ClearReply();
        VoiceStatus.Text = "—";

        _isRoomHost = null;
        ResetDebug();
        SetStatus("Status_Ready", StatusKind.Idle);
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
        _room.StatusChanged += msg => Dispatch(() => { _statusKey = null; StatusLabel.Text = msg; });
        _p2p.StatusChanged  += msg => Dispatch(() => { _statusKey = null; StatusLabel.Text = msg; });

        _p2p.PeerConnected += info => Dispatch(() =>
        {
            DbgPeers.Text = _p2p.PeerCount.ToString();
            SetStatus("St_Connected", StatusKind.Ok);
            SetRoomControlsEnabled(true);
            RefreshMemberList();
        });

        _p2p.EncryptionEstablished += (peerId, ok) => Dispatch(() =>
        {
            _encryptionOk            = ok;
            DbgEncryption.Text       = ok ? "AES-256-GCM" : Loc.T("Enc_KeyError");
            DbgEncIcon.Kind          = ok ? PackIconLucideKind.ShieldCheck : PackIconLucideKind.ShieldAlert;
            var brush                = ok ? Res("Ok") : Res("Danger");
            DbgEncryption.Foreground = brush;
            DbgEncIcon.Foreground    = brush;

            if (!ok)
                Notify(Loc.T("Enc_FailTitle"), Loc.T("Enc_FailBody"), ToastKind.Error);
        });

        _p2p.PeerDisconnected += (id, reason) => Dispatch(() =>
        {
            DbgPeers.Text = _p2p.PeerCount.ToString();
            if (_p2p.PeerCount == 0 && _room.IsActive)
            {
                SetStatus("St_Disconnected", StatusKind.Idle);
                SetRoomControlsEnabled(false);
                Notify(Loc.T("Room_AllLeftTitle"), Loc.T("Room_AllLeftBody"), ToastKind.Warn, alsoTray: true);
            }
            RefreshMemberList();
        });

        _p2p.ConnectionFailed += (title, msg) => Dispatch(() =>
        {
            SetStatus("St_Error", StatusKind.Danger);
            SetBusy(false);
            ShowToast(title, msg, ToastKind.Error);
        });

        _p2p.ConnectionRejected += reason => Dispatch(() =>
        {
            SetBusy(false);
            ShowToast(Loc.T("Room_RejectTitle"), reason, ToastKind.Error);
        });

        _p2p.MessageReceived += (id, frame) => Dispatch(RefreshMemberList);

        _room.MemberJoined += m => Dispatch(() =>
        {
            RefreshMemberList();
            // The local user's own join record arrives through this event too; don't
            // announce the user to themselves.
            if (!IsSelf(m.Username))
                Notify(Loc.T("Room_JoinedTitle"), Loc.T("Room_JoinedBody", m.Username), ToastKind.Success, alsoTray: true);
        });

        _room.MemberLeft += m => Dispatch(() =>
        {
            RefreshMemberList();
            if (!IsSelf(m.Username))
                Notify(Loc.T("Room_LeftTitle"), Loc.T("Room_LeftBody", m.Username));
        });

        _room.RoomClosed += msg => Dispatch(() =>
        {
            _room.Shutdown();
            _voice.Reset();
            LeaveRoom();
            Announce(msg, Loc.T("Room_ClosedTitle"), MessageBoxImage.Information);
        });

        _room.ModerationReceived += (op, on) => Dispatch(() => ApplyModeration(op, on));

        // ── Screen Share ──────────────────────────────────────────────────────
        _screenShare.FrameCaptured += bytes =>
        {
            _room.BroadcastScreenShareFrame(bytes);
            Dispatch(() =>
            {
                if (ShareViewArea.Visibility == Visibility.Visible)
                {
                    SharePlaceholder.Visibility = Visibility.Collapsed;
                    ScreenShareImg.Source       = Decode(bytes);
                }
            });
        };

        _screenShare.AudioCaptured += SendAudio;

        _room.ScreenShareAudioReceived += packet => Dispatch(() => PlayAudio(packet));

        _room.ScreenShareStarted += username => Dispatch(() =>
        {
            _remoteSharerUsername    = username;
            ScreenShareBtn.IsEnabled = false;
            OpenScreenShareTab();
            Notify(Loc.T("Share_StartedTitle"), Loc.T("Share_StartedBody", username),
                   ToastKind.Info, alsoTray: true);
        });

        _room.ScreenShareFrame += (_, bytes) => Dispatch(() =>
        {
            if (ShareViewArea.Visibility == Visibility.Visible)
            {
                SharePlaceholder.Visibility = Visibility.Collapsed;
                ScreenShareImg.Source       = Decode(bytes);
            }
        });

        _room.ScreenShareStopped += username => Dispatch(() =>
        {
            _remoteSharerUsername = null;
            StopAudioPlayback();
            if (_p2p.PeerCount > 0) ScreenShareBtn.IsEnabled = true;
            Notify(Loc.T("Share_EndedTitle"), Loc.T("Share_EndedBody", username));

            SetSharePlaceholder("Share_Stopped");
            SharePlaceholder.Visibility = Visibility.Visible;
            ScreenShareImg.Source       = null;
            _ = Task.Delay(1500).ContinueWith(_ => Dispatch(CloseScreenShareTab));
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
                Status      = Loc.T("File_ReadyToDownload"),
                CanDownload = true
            };
            _fileNotifs[info.Id] = notif;
            FileList.Items.Add(notif);
            if (_activeTab != "files") SetActiveTab("files");
            FileList.ScrollIntoView(notif);
            Activate();
            VoiceStatus.Text = Loc.T("File_Incoming", info.FileName);
            Notify(Loc.T("File_ArrivedTitle"), Loc.T("File_ArrivedBody", info.FileName, FormatSize(info.Size)),
                   ToastKind.Info, alsoTray: true);
        });

        _file.TransferAccepted += id => Dispatch(() =>
        {
            if (_fileNotifs.TryGetValue(id, out var notif) && notif.IsSent)
                notif.Status = Loc.T("File_Sending");
        });

        _file.TransferProgress += (id, done, total) => Dispatch(() =>
        {
            int pct = total > 0 ? (int)(done * 100.0 / total) : 100;
            if (_fileNotifs.TryGetValue(id, out var notif))
                notif.Status = Loc.T(notif.IsSent ? "File_SendProgress" : "File_RecvProgress", pct, done, total);
            VoiceStatus.Text = Loc.T("File_TransferPct", pct);
        });

        _file.TransferComplete += (id, path) => Dispatch(() =>
        {
            string name = System.IO.Path.GetFileName(path);
            if (_fileNotifs.TryGetValue(id, out var notif))
            {
                bool sent = notif.IsSent;
                notif.Status      = Loc.T(sent ? "File_SentMark" : "File_SavedMark");
                notif.CanDownload = false;
                _fileNotifs.Remove(id);
                VoiceStatus.Text = Loc.T(sent ? "File_SentStatus" : "File_SavedStatus", name);

                if (sent)
                    Notify(Loc.T("File_SentTitle"), Loc.T("File_SentBody", name), ToastKind.Success);
                else
                {
                    PlayDownloadSound();
                    Notify(Loc.T("File_DoneTitle"), Loc.T("File_DoneBody", name),
                           ToastKind.Success, alsoTray: true);
                }
            }
            else VoiceStatus.Text = Loc.T("File_CompleteStatus", name);
        });

        _file.TransferFailed += (id, reason) => Dispatch(() =>
        {
            bool sent = false;
            if (_fileNotifs.TryGetValue(id, out var notif))
            {
                sent              = notif.IsSent;
                notif.Status      = Loc.T(sent ? "File_SendFailMark" : "File_RecvFailMark", reason);
                notif.CanDownload = false;
                _fileNotifs.Remove(id);
            }
            VoiceStatus.Text = Loc.T("File_ErrStatus", reason);
            Notify(Loc.T(sent ? "File_SendFailTitle" : "File_RecvFailTitle"), reason,
                   ToastKind.Error, alsoTray: true);
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
        SetStatus("St_CreatingRoom", StatusKind.Warn);

        string username = UsernameBox.Text.Trim() is { Length: > 0 } u ? u : Loc.T("Room_DefaultHost");
        Save(UsernamePath, username);
        _chat.SetUsername(username);

        try
        {
            ushort port = GetPort();
            // The startup firewall rule only opens the default port — re-apply it for
            // whatever port the user actually configured, or a guest can't reach a
            // host that changed it, with nothing telling either side why.
            await VirtualLANPlatform.App.EnsureFirewallRuleAsync(port);

            var (ok, localIp, _) = await _room.CreateRoomAsync(
                username, port: port, localIp: GetSelectedAdapterIP(), ct: _opCts!.Token);

            if (ok)
            {
                DbgRole.Text = "Host";
                RefreshMemberList();
                EnterRoom(localIp, isHost: true);
                Notify(Loc.T("Room_ReadyTitle"), Loc.T("Room_ReadyBody", localIp), ToastKind.Success);
            }
            else
            {
                SetStatus("St_RoomFailed", StatusKind.Danger);
                Notify(Loc.T("Room_FailTitle"), Loc.T("Room_FailBody"), ToastKind.Error);
            }
        }
        catch (OperationCanceledException)
        {
            _room.Shutdown();
            SetStatus("St_Cancelled", StatusKind.Idle);
        }
        finally { SetBusy(false); }
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        // Accept "ip", "ip:port", or a "vlan://ip:port" invite string. An explicit
        // port in the field wins over the port box; otherwise fall back to it.
        string raw = JoinCodeBox.Text.Trim();
        if (raw.StartsWith("vlan://", StringComparison.OrdinalIgnoreCase))
            raw = raw["vlan://".Length..].Trim().TrimEnd('/');

        if (raw.Length == 0)
        {
            Notify(Loc.T("Room_NoIpTitle"), Loc.T("Room_NoIpBody"), ToastKind.Warn);
            return;
        }

        string hostIp;
        ushort port;
        int colon = raw.LastIndexOf(':');
        if (colon > 0 && colon < raw.Length - 1 &&
            ushort.TryParse(raw[(colon + 1)..], out ushort parsedPort) && parsedPort >= 1000)
        {
            hostIp = raw[..colon];
            port   = parsedPort;
        }
        else
        {
            hostIp = raw;
            port   = GetPort();
        }

        SetBusy(true);
        SetStatus("St_Connecting", StatusKind.Warn);

        string username = UsernameBox.Text.Trim() is { Length: > 0 } u ? u : Loc.T("Room_DefaultGuest");
        Save(UsernamePath, username);
        _chat.SetUsername(username);

        try
        {
            bool ok = await _room.JoinRoomAsync(hostIp, port, username, _opCts!.Token);
            if (ok)
            {
                DbgRole.Text = "Guest";
                RefreshMemberList();
                EnterRoom(hostIp, isHost: false);
                Notify(Loc.T("Room_JoinedOkTitle"), Loc.T("Room_JoinedOkBody", hostIp), ToastKind.Success);
            }
            else
            {
                SetStatus("St_JoinFailed", StatusKind.Danger);
                Notify(Loc.T("St_JoinFailed"), Loc.T("Room_JoinFailBody"), ToastKind.Error);
            }
        }
        catch (OperationCanceledException)
        {
            _room.Shutdown();
            SetStatus("St_Cancelled", StatusKind.Idle);
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

        SaveText.Text = Loc.T("Common_Saved");
        SaveIcon.Kind = PackIconLucideKind.Check;
        SaveUsernameBtn.Foreground = Res("Ok");

        try
        {
            await Task.Delay(1800, cts.Token);
            SaveText.Text = Loc.T("Common_Save");
        if (_sharePlaceholderKey is { Length: > 0 } shareKey)
            SharePlaceholder.Text = Loc.T(shareKey);
            SaveIcon.Kind = PackIconLucideKind.Save;
            SaveUsernameBtn.ClearValue(ForegroundProperty);
        }
        catch (OperationCanceledException) { }
    }

    private void UsernameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        string username = UsernameBox.Text.Trim();
        if (username.Length == 0) return;
        Save(UsernamePath, username);
    }

    private void UsernameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveUsername_Click(sender, e);
        e.Handled = true;
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
            Loc.T(isHost ? "Room_Close" : "Room_Leave"),
            Loc.T(isHost ? "Room_CloseConfirmBody" : "Room_LeaveConfirmBody"),
            Loc.T(isHost ? "Room_Close" : "Room_LeaveConfirmBtn"),
            Loc.T("Common_Cancel"), isDanger: true);
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
            ? Loc.T("Mod_MicLocked")
            : active ? Loc.T("Mod_MicOn") : Loc.T("Mod_MicOff");
    }

    private void SetSpeakerVisual(bool active)
    {
        SpeakerIcon.Kind      = active ? PackIconLucideKind.Volume2 : PackIconLucideKind.VolumeOff;
        SpeakerBtn.Background = active ? Res("OkGrad") : Res("DangerGrad");
        SpeakerBtn.ToolTip    = active ? Loc.T("Mod_SpkOn") : Loc.T("Mod_SpkOff");
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
                if (on) Notify(Loc.T("Mod_MutedTitle"), Loc.T("Mod_MutedBody"),
                               ToastKind.Warn, alsoTray: true);
                else    Notify(Loc.T("Mod_UnmutedTitle"), Loc.T("Mod_UnmutedBody"),
                               ToastKind.Success, alsoTray: true);
                break;

            case "stopshare":
                if (_screenShare.IsSharing)
                {
                    _screenShare.Stop();
                    _room.BroadcastScreenShareStop();
                    SetShareButton(sharing: false);
                    CloseScreenShareTab();
                    VoiceStatus.Text = Loc.T("Mod_ShareKilled");
                    Notify(Loc.T("Share_KilledTitle"), Loc.T("Share_KilledBody"),
                           ToastKind.Warn, alsoTray: true);
                }
                break;

            case "kick":
                _room.Shutdown();
                _voice.Reset();
                LeaveRoom();
                Announce(Loc.T("Mod_KickedBody"), Loc.T("Mod_KickedTitle"), MessageBoxImage.Warning);
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
        VoiceStatus.Text = mute ? Loc.T("Mod_MutedPeer", m.Username) : Loc.T("Mod_UnmutedPeer", m.Username);
    }

    private void ModStopShare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemberItem m }) return;
        if (_room.StopMemberShare(m.Username))
            VoiceStatus.Text = Loc.T("Mod_StopShareSent", m.Username);
    }

    private async void ModKick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemberItem m }) return;

        bool confirmed = await ShowModal(Loc.T("Mod_KickTitle"), Loc.T("Mod_KickBody", m.Username),
            Loc.T("Mod_KickConfirm"), Loc.T("Common_Cancel"), isDanger: true);
        if (!confirmed) return;

        if (_room.KickMember(m.Username))
        {
            _mutedMembers.Remove(m.Username);
            VoiceStatus.Text = Loc.T("Mod_KickedPeer", m.Username);
        }
    }

    // ── File transfer ─────────────────────────────────────────────────────────

    private void SendFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = Loc.T("File_PickerTitle") };
        if (dlg.ShowDialog() != true) return;
        BeginSendFile(dlg.FileName);
    }

    // ── Drag a file onto the chat area to send it ─────────────────────────────

    private void ChatArea_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = SendFileBtn.IsEnabled && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void ChatArea_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        e.Handled = true;

        if (!SendFileBtn.IsEnabled)
        {
            Notify(Loc.T("File_NoPeersTitle"), Loc.T("File_NoPeersBody"), ToastKind.Warn);
            return;
        }

        var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
        foreach (string path in paths.Where(System.IO.File.Exists))
            BeginSendFile(path);
    }

    private void BeginSendFile(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        long   size = new System.IO.FileInfo(path).Length;

        if (size == 0)
        {
            Notify(Loc.T("File_EmptyTitle"), Loc.T("File_EmptyBody", name), ToastKind.Warn);
            return;
        }

        string transferId = Guid.NewGuid().ToString("N")[..12];
        var notif = new FileNotification
        {
            Id = transferId, FileName = name, SizeText = FormatSize(size),
            IsSent = true, Status = Loc.T("File_WaitingAccept"), CanDownload = false
        };
        _fileNotifs[transferId] = notif;
        FileList.Items.Add(notif);
        SetActiveTab("files");
        FileList.ScrollIntoView(notif);

        _ = Task.Run(async () =>
        {
            try { await _file.SendFileAsync(path, transferId); }
            catch (Exception ex)
            {
                // SendFileAsync already reports failures via TransferFailed, but guard
                // against anything it throws before reaching its own try block too —
                // otherwise this fire-and-forget task would fault silently and the
                // card would stay stuck at Loc.T("File_WaitingAccept") forever.
                Dispatch(() =>
                {
                    if (_fileNotifs.TryGetValue(transferId, out var n))
                    {
                        n.Status      = Loc.T("File_SendExFail", ex.Message);
                        n.CanDownload = false;
                        _fileNotifs.Remove(transferId);
                    }
                });
            }
        });
    }

    private void DownloadFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileNotification notif }) return;
        notif.CanDownload = false;
        notif.Status      = Loc.T("File_Downloading");
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
        string text = EmojiComposer.GetPlainText(ChatInput).Trim();
        if (text.Length == 0) return;

        if (!_p2p.IsRunning)
        {
            Notify(Loc.T("Chat_NotInRoomTitle"), Loc.T("Chat_NotInRoomBody"), ToastKind.Warn);
            return;
        }

        if (_fileReplyNotif != null)
        {
            var stub = new ChatMessage
            {
                Id     = "file:" + _fileReplyNotif.Id,
                Sender = Loc.T("File_ChatBadge"),
                Text   = _fileReplyNotif.FileName,
                SentAt = DateTime.Now
            };
            _chat.SendToAll(text, stub);
        }
        else
        {
            _chat.SendToAll(text, _replyTarget);
        }
        EmojiComposer.Clear(ChatInput);
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

        // Notify only when the message is from someone else and the user isn't
        // looking (window inactive or minimized).
        if (!msg.IsOwn && (!IsActive || WindowState == WindowState.Minimized))
        {
            string preview = msg.Text.Length > 120 ? msg.Text[..120] + "…" : msg.Text;
            ShowNotification(Loc.T("Chat_NewFrom", msg.Sender), preview);
        }
    }

    private void ReplyTo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatMessage msg }) return;

        _replyTarget       = msg;
        ReplyToName.Text   = Loc.T("Chat_ReplyTo", msg.Sender);
        ReplyToText.Text   = msg.Preview;
        ReplyBar.Visibility = Visibility.Visible;
        ChatInput.Focus();
    }

    /// <summary>
    /// Bubbles render through a TextBlock, which has no caret selection — this is how
    /// the text gets out. See <see cref="UI.TextBlockHelper"/> for why not a RichTextBox.
    /// </summary>
    private void CopyMsg_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatMessage msg }) return;
        try
        {
            Clipboard.SetText(msg.Text);
            ShowToast(Loc.T("Common_Copied"), Loc.T("Chat_CopiedBody"), ToastKind.Success);
        }
        catch { }
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
        ReplyToName.Text    = Loc.T("File_ReplyTo");
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

        bool confirmed = await ShowModal(Loc.T("Chat_DeleteTitle"), Loc.T("Chat_DeleteBody"),
            Loc.T("Chat_DeleteConfirm"), Loc.T("Common_Cancel"), isDanger: true);
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

    private void InsertEmoji(string emoji) => EmojiComposer.InsertAtCaret(ChatInput, emoji);

    private bool _suppressChatInputChanged;

    private void ChatInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressChatInputChanged) return;
        _suppressChatInputChanged = true;
        try { EmojiComposer.TryConvertNearCaret(ChatInput); }
        finally { _suppressChatInputChanged = false; }
    }

    // ── Screen share ──────────────────────────────────────────────────────────

    private void OpenScreenShareTab()
    {
        ScreenShareImg.Source       = null;
        SetSharePlaceholder("Share_Waiting");
        SharePlaceholder.Visibility = Visibility.Visible;
        TabShareBtn.Visibility      = Visibility.Visible;
        SetActiveTab("share");
    }

    private void CloseScreenShareTab()
    {
        ScreenShareImg.Source  = null;
        TabShareBtn.Visibility = Visibility.Collapsed;
        if (_activeTab == "share") SetActiveTab("chat");
    }

    private bool _shareButtonSharing;

    private void SetShareButton(bool sharing)
    {
        _shareButtonSharing = sharing;
        ScreenShareIcon.Kind = sharing ? PackIconLucideKind.MonitorStop : PackIconLucideKind.MonitorUp;
        ScreenShareText.Text = sharing ? Loc.T("Share_StopAction") : Loc.T("Share_Tab");
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
            CloseScreenShareTab();
        }
        else
        {
            var picker = new WindowPickerDialog { Owner = this };
            if (picker.ShowDialog() != true) return;

            _room.BroadcastScreenShareStart();
            _screenShare.Start(fps: 10,
                windowHandle: picker.SelectedHandle,
                shareAudio:   picker.ShareAudio);

            SetShareButton(sharing: true);
            OpenScreenShareTab();
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

    // ── Tab management ────────────────────────────────────────────────────────

    private void SetActiveTab(string tab)
    {
        _activeTab = tab;

        if (tab != "chat" && EmojiPopup.IsOpen)
            EmojiPopup.IsOpen = false;

        ChatList.Visibility      = tab == "chat"  ? Visibility.Visible : Visibility.Collapsed;
        FileList.Visibility      = tab == "files" ? Visibility.Visible : Visibility.Collapsed;
        ShareViewArea.Visibility = tab == "share" ? Visibility.Visible : Visibility.Collapsed;

        bool chatActive = tab == "chat";
        Composer.Visibility = chatActive ? Visibility.Visible : Visibility.Collapsed;
        if (!chatActive) ReplyBar.Visibility = Visibility.Collapsed;

        TabChatBtn.Style  = (Style)FindResource(tab == "chat"  ? "Tab.Active" : "Tab.Btn");
        TabFilesBtn.Style = (Style)FindResource(tab == "files" ? "Tab.Active" : "Tab.Btn");
        TabShareBtn.Style = (Style)FindResource(tab == "share" ? "Tab.Active" : "Tab.Btn");
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string t }) SetActiveTab(t);
    }

    private void RefreshMembers_Click(object sender, RoutedEventArgs e) => RefreshMemberList();

    // ── Link navigation ───────────────────────────────────────────────────────

    private async void OnLinkNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        string url = e.Uri.AbsoluteUri;
        bool ok = await ShowModal(Loc.T("Link_OpenTitle"), url, Loc.T("Link_OpenBtn"), Loc.T("Common_Cancel"));
        if (!ok) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true
            });
        }
        catch { }
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
                Username     = m.Username,
                IsSelf       = isSelf,
                IsHostRole   = _room.IsHost && m.PeerId == -1,
                CanModerate  = _room.IsHost && !isSelf && m.PeerId >= 0,
                IsMuted      = _mutedMembers.Contains(m.Username)
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
        _encryptionOk            = null;
        DbgEncryption.Text       = Loc.T("Conn_NoEncryption");
        DbgEncIcon.Kind          = PackIconLucideKind.ShieldOff;
        DbgEncryption.Foreground = Res("Warn");
        DbgEncIcon.Foreground    = Res("Warn");
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private enum StatusKind { Idle, Ok, Warn, Danger }

    /// <summary>
    /// The key behind the status pill, or null when a manager pushed raw text.
    /// Writing StatusLabel.Text replaces the XAML binding that was translating it, so
    /// without remembering the key the pill freezes in whichever language it was set in
    /// and never follows a later switch.
    /// </summary>
    private string? _statusKey;

    private void SetStatus(string key, StatusKind kind)
    {
        _statusKey       = key;
        StatusLabel.Text = Loc.T(key);
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
        if (ushort.TryParse(PortBox.Text.Trim(), out ushort p) && p >= 1000) return p;
        PortBox.Text = "42777";
        Save(PortPath, "42777");
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
            await ShowModal(title, message, Loc.T("Common_Ok"), null, icon == MessageBoxImage.Warning)));

    // ── Language ──────────────────────────────────────────────────────────────

    private void Lang_Click(object sender, RoutedEventArgs e) => Loc.I.Toggle();

    /// <summary>
    /// Re-applies text that code set once and bindings cannot reach. Everything declared
    /// in XAML updates itself through the indexer binding; these are the leftovers that a
    /// method wrote directly, and without this pass they would stay in the old language
    /// until whatever produced them happened to run again.
    /// </summary>
    private void OnLanguageChanged()
    {
        if (_statusKey is { Length: > 0 } statusKey) StatusLabel.Text = Loc.T(statusKey);

        // Each of these is written directly by code somewhere, which replaces the XAML
        // binding that was translating it. Re-running the setters is what keeps them
        // following the language instead of freezing at whatever it was first set in.
        SetMicVisual(_voice.IsMicActive);
        SetSpeakerVisual(!_voice.IsSpeakerMuted);
        SetShareButton(_shareButtonSharing);
        SaveText.Text = Loc.T("Common_Save");
        if (_sharePlaceholderKey is { Length: > 0 } shareKey)
            SharePlaceholder.Text = Loc.T(shareKey);

        DbgEncryption.Text = _encryptionOk switch
        {
            true  => "AES-256-GCM",
            false => Loc.T("Enc_KeyError"),
            null  => Loc.T("Conn_NoEncryption")
        };

        if (_vlan.IsActive)
            VlanRoleText.Text = Loc.T(_vlan.IsHost ? "Vlan_RoleHost" : "Vlan_RoleGuest");

        if (_isRoomHost is { } host)
            LeaveCloseText.Text = Loc.T(host ? "Room_Close" : "Room_Leave");

        // Member rows are built in code, so they carry their labels with them.
        RefreshMemberList();
    }

    /// <summary>Null outside a room; otherwise whether this user is hosting it.</summary>
    private bool? _isRoomHost;

    /// <summary>Null before any key exchange; otherwise whether it succeeded.</summary>
    private bool? _encryptionOk;

    /// <summary>
    /// Key behind the screen-share placeholder. Unlike the other overlays, the share tab
    /// leaves the language button reachable, so this one really can be switched underneath
    /// the user while it is on screen.
    /// </summary>
    private string? _sharePlaceholderKey;

    private void SetSharePlaceholder(string key)
    {
        _sharePlaceholderKey  = key;
        SharePlaceholder.Text = Loc.T(key);
    }

    // ── Update gate ───────────────────────────────────────────────────────────

    private UpdateInfo?              _pendingUpdate;
    private CancellationTokenSource? _updateCts;
    private Storyboard?              _updateSweep;

    private void CheckUpdate_Click(object sender, RoutedEventArgs e) => _ = RunUpdateGate(startup: false);

    /// <summary>
    /// Shows the gate, checks the manifest, and settles on one of three outcomes:
    /// up to date, an update to install, or a failed check.
    /// </summary>
    /// <param name="startup">
    /// True for the automatic check as the app opens. A failed check is then silent —
    /// being offline must not keep the user out of their own app. A manual check always
    /// reports what happened, because the user asked and deserves an answer either way.
    /// </param>
    private async Task RunUpdateGate(bool startup)
    {
        if (UpdateGate.Visibility == Visibility.Visible) return;

        _updateCts?.Cancel();
        _updateCts = new CancellationTokenSource();
        var ct = _updateCts.Token;

        ShowUpdateGate();
        SetUpdateChecking();

        // A check that returns instantly reads as a glitch; hold the frame briefly so
        // the user sees that something was actually looked at.
        var delay  = Task.Delay(startup ? 900 : 600, ct);
        var check  = UpdateService.CheckAsync(ct);
        UpdateInfo info;
        try
        {
            await Task.WhenAll(delay, check);
            info = check.Result;
        }
        catch (OperationCanceledException) { return; }

        StopUpdateSweep();

        switch (info.State)
        {
            case UpdateState.Available:
                _pendingUpdate = info;
                SetUpdateAvailable(info);
                break;

            case UpdateState.UpToDate when !startup:
                SetUpdateUpToDate(info);
                break;

            case UpdateState.Failed when !startup:
                SetUpdateFailed(info);
                break;

            // On startup there is nothing worth stopping for: already current, or the
            // check couldn't run. Being offline must not keep the user out of the app.
            default:
                HideUpdateGate();
                break;
        }
    }

    private void ShowUpdateGate()
    {
        UpdateGate.Visibility = Visibility.Visible;
        UpdateGate.BeginAnimation(OpacityProperty, null);
        UpdateGate.Opacity = 1;
    }

    private void HideUpdateGate()
    {
        StopUpdateSweep();
        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromSeconds(0.25)))
                   { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) =>
        {
            UpdateGate.BeginAnimation(OpacityProperty, null);
            UpdateGate.Opacity    = 1;
            UpdateGate.Visibility = Visibility.Collapsed;
        };
        UpdateGate.BeginAnimation(OpacityProperty, fade);
    }

    private void SetUpdateChecking()
    {
        UpdateTitle.Text          = Loc.T("Update_Checking");
        UpdateMessage.Text        = Loc.T("Update_CheckingBody");
        UpdateVersions.Visibility = Visibility.Collapsed;
        UpdateButtons.Visibility  = Visibility.Collapsed;
        UpdateNotesLink.Visibility = Visibility.Collapsed;
        UpdateHint.Visibility     = Visibility.Collapsed;
        UpdateBarTrack.Visibility = Visibility.Visible;
        StartUpdateSweep();
    }

    private void SetUpdateUpToDate(UpdateInfo info)
    {
        UpdateTitle.Text           = Loc.T("Update_CurrentTitle");
        UpdateMessage.Text         = Loc.T("Update_CurrentBody", info.Current);
        UpdateVersions.Visibility  = Visibility.Collapsed;
        UpdateNotesLink.Visibility = Visibility.Collapsed;
        UpdateHint.Visibility      = Visibility.Collapsed;
        UpdateBarTrack.Visibility  = Visibility.Collapsed;

        UpdateNowBtn.Visibility   = Visibility.Collapsed;
        UpdateLaterBtn.Visibility = Visibility.Collapsed;
        UpdateCloseBtn.Visibility = Visibility.Visible;
        UpdateButtons.Visibility  = Visibility.Visible;
    }

    private void SetUpdateAvailable(UpdateInfo info)
    {
        UpdateTitle.Text   = Loc.T("Update_NewTitle");
        UpdateMessage.Text = Loc.T(info.Mandatory ? "Update_NewMandatory" : "Update_NewOptional");

        UpdateFromText.Text       = $"v{info.Current}";
        UpdateToText.Text         = $"v{info.Latest}";
        UpdateVersions.Visibility = Visibility.Visible;

        UpdateBarTrack.Visibility = Visibility.Collapsed;
        UpdateHint.Visibility     = Visibility.Collapsed;

        UpdateNowBtn.Visibility   = Visibility.Visible;
        UpdateCloseBtn.Visibility = Visibility.Collapsed;
        // A mandatory release leaves no way past the gate.
        UpdateLaterBtn.Visibility = info.Mandatory ? Visibility.Collapsed : Visibility.Visible;
        UpdateButtons.Visibility  = Visibility.Visible;

        if (Uri.TryCreate(info.Changelog, UriKind.Absolute, out var notes))
        {
            UpdateNotesHyper.NavigateUri = notes;
            UpdateNotesLink.Visibility   = Visibility.Visible;
        }
        else UpdateNotesLink.Visibility = Visibility.Collapsed;
    }

    private void SetUpdateFailed(UpdateInfo info)
    {
        UpdateTitle.Text           = Loc.T("Update_FailTitle");
        UpdateMessage.Text         = Loc.T("Update_FailBody");
        UpdateVersions.Visibility  = Visibility.Collapsed;
        UpdateNotesLink.Visibility = Visibility.Collapsed;
        UpdateBarTrack.Visibility  = Visibility.Collapsed;

        UpdateHint.Text       = info.Error ?? "";
        UpdateHint.Visibility = string.IsNullOrWhiteSpace(info.Error)
            ? Visibility.Collapsed : Visibility.Visible;

        UpdateNowBtn.Visibility   = Visibility.Collapsed;
        UpdateLaterBtn.Visibility = Visibility.Collapsed;
        UpdateCloseBtn.Visibility = Visibility.Visible;
        UpdateButtons.Visibility  = Visibility.Visible;
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate?.Url is not { } url) return;

        UpdateTitle.Text          = Loc.T("Update_DlTitle");
        UpdateMessage.Text        = Loc.T("Update_DlBody");
        UpdateButtons.Visibility  = Visibility.Collapsed;
        UpdateVersions.Visibility = Visibility.Collapsed;
        UpdateBarTrack.Visibility = Visibility.Visible;
        UpdateHint.Text           = Loc.T("Update_DlPercent", 0);
        UpdateHint.Visibility     = Visibility.Visible;
        StopUpdateSweep();
        SetUpdateProgress(0);

        var progress = new Progress<double>(p =>
        {
            SetUpdateProgress(p);
            UpdateHint.Text = Loc.T("Update_DlPercent", (int)p);
        });

        string? installer = await UpdateService.DownloadAsync(url, progress);

        if (installer == null)
        {
            SetUpdateFailed(new UpdateInfo(UpdateState.Failed, Error: Loc.T("Update_DlFailReason")));
            UpdateTitle.Text   = Loc.T("Update_DlFailTitle");
            UpdateMessage.Text = Loc.T("Update_DlFailBody");
            UpdateNowBtn.Visibility   = Visibility.Visible;
            UpdateCloseBtn.Visibility = _pendingUpdate.Mandatory
                ? Visibility.Collapsed : Visibility.Visible;
            UpdateButtons.Visibility  = Visibility.Visible;
            return;
        }

        UpdateTitle.Text   = Loc.T("Update_ReadyTitle");
        UpdateMessage.Text = Loc.T("Update_ReadyBody");
        UpdateHint.Visibility = Visibility.Collapsed;

        if (!UpdateService.RunInstaller(installer))
        {
            SetUpdateFailed(new UpdateInfo(UpdateState.Failed, Error: Loc.T("Update_RunRefused")));
            UpdateTitle.Text   = Loc.T("Update_RunFailTitle");
            UpdateMessage.Text = Loc.T("Update_RunFailBody");
            UpdateNowBtn.Visibility  = Visibility.Visible;
            UpdateButtons.Visibility = Visibility.Visible;
            return;
        }

        // The installer can't overwrite files this process holds open.
        await Task.Delay(700);
        System.Windows.Application.Current.Shutdown();
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => HideUpdateGate();

    /// <summary>Indeterminate sweep: a short bar sliding across the track while we wait.</summary>
    private void StartUpdateSweep()
    {
        StopUpdateSweep();
        UpdateBarFill.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        UpdateBarFill.Width = 110;

        var slide = new System.Windows.Media.TranslateTransform(-110, 0);
        UpdateBarFill.RenderTransform = slide;

        var anim = new DoubleAnimation(-110, 430, new Duration(TimeSpan.FromSeconds(1.1)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(anim, UpdateBarFill);
        Storyboard.SetTargetProperty(anim,
            new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));

        _updateSweep = new Storyboard();
        _updateSweep.Children.Add(anim);
        _updateSweep.Begin();
    }

    private void StopUpdateSweep()
    {
        _updateSweep?.Stop();
        _updateSweep = null;
        UpdateBarFill.RenderTransform = null;
        UpdateBarFill.Width = 0;
    }

    private void SetUpdateProgress(double percent)
    {
        double track = UpdateBarTrack.ActualWidth;
        if (track <= 0) track = 360;
        UpdateBarFill.Width = Math.Clamp(percent, 0, 100) / 100.0 * track;
    }

    private bool _shutdownComplete;

    /// <summary>
    /// Holds the window open until the virtual LAN is actually down.
    /// <para>
    /// Closing tears down a network adapter, tells peers goodbye and withdraws a port
    /// mapping from the router — seconds of blocking work that cannot run on the UI
    /// thread, and that the process exiting would cut short. Leaving it unfinished
    /// stranded a virtual adapter and an open port on the router after every exit.
    /// </para>
    /// </summary>
    private async void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return; // second pass: teardown is done, let it close

        e.Cancel = true;
        ShutdownOverlay.Visibility = Visibility.Visible;

        // UI-thread-owned pieces first, while we still are on it.
        _memberTimer.Stop();
        StopAudioPlayback();
        _trayIcon?.Dispose();

        var teardown = Task.Run(() =>
        {
            // Each guarded separately: one service failing must not strand the rest.
            try { _vlan.Dispose(); }        catch { }
            try { _portProbe.Dispose(); }   catch { }
            try { _screenShare.Dispose(); } catch { }
            try { _file.Dispose(); }        catch { }
            try { _voice.Dispose(); }       catch { }
            try { _chat.Dispose(); }        catch { }
            try { _room.Dispose(); }        catch { }
        });

        // A hung netsh or an unresponsive router must not trap the user in a window
        // that will not close. Give the teardown a bounded window, then go regardless.
        await Task.WhenAny(teardown, Task.Delay(TimeSpan.FromSeconds(8)));

        _shutdownComplete = true;
        Close();
    }
}

/// <summary>One row in the member sidebar, with the host's moderation affordances.</summary>
public sealed class MemberItem : INotifyPropertyChanged
{
    public required string Username    { get; init; }
    public          bool   IsSelf      { get; init; }
    public          bool   IsHostRole  { get; init; }
    public          bool   CanModerate { get; init; }

    public string     Initial       => Username.TrimStart() is { Length: > 0 } s ? s[..1].ToUpperInvariant() : Loc.T("Member_Unknown");
    public Visibility ModVisibility => CanModerate ? Visibility.Visible : Visibility.Collapsed;

    public string     RoleLabel      => IsSelf ? Loc.T("Member_You") : IsHostRole ? Loc.T("Member_HostBadge") : "";
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
    public string             MuteTip  => _isMuted ? Loc.T("Mod_UnmuteAction") : Loc.T("Mod_MuteAction");

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
