using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Application  = System.Windows.Application;
using MessageBox   = System.Windows.MessageBox;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;

namespace VirtualLANPlatform.UI.ViewModels;

public class TestViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly P2PManager _p2p = new();

    // ── Bindable properties ───────────────────────────────────────────────────

    private string _username = "کاربر";
    public string Username
    {
        get => _username;
        set { _username = value; OnPropertyChanged(); }
    }

    private string _connectionCode = "";
    public string ConnectionCode
    {
        get => _connectionCode;
        set { _connectionCode = value; OnPropertyChanged(); }
    }

    private string _joinCode = "";
    public string JoinCode
    {
        get => _joinCode;
        set { _joinCode = value; OnPropertyChanged(); }
    }

    private string _statusText = "آماده";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private string _statusColor = "#43B581";
    public string StatusColor
    {
        get => _statusColor;
        set { _statusColor = value; OnPropertyChanged(); }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    // Debug info
    private string _debugLocalIP   = "—";
    private string _debugPublicIP  = "—";
    private string _debugLocalPort = "—";
    private string _debugExtPort   = "—";
    private string _debugNatType   = "—";
    private string _debugPeerCount = "0";
    private string _debugRole      = "—";

    public string DebugLocalIP   { get => _debugLocalIP;   set { _debugLocalIP   = value; OnPropertyChanged(); } }
    public string DebugPublicIP  { get => _debugPublicIP;  set { _debugPublicIP  = value; OnPropertyChanged(); } }
    public string DebugLocalPort { get => _debugLocalPort; set { _debugLocalPort = value; OnPropertyChanged(); } }
    public string DebugExtPort   { get => _debugExtPort;   set { _debugExtPort   = value; OnPropertyChanged(); } }
    public string DebugNatType   { get => _debugNatType;   set { _debugNatType   = value; OnPropertyChanged(); } }
    public string DebugPeerCount { get => _debugPeerCount; set { _debugPeerCount = value; OnPropertyChanged(); } }
    public string DebugRole      { get => _debugRole;      set { _debugRole      = value; OnPropertyChanged(); } }

    public ObservableCollection<string> Members { get; } = [];
    public ObservableCollection<string> LogEntries { get; } = [];

    // ── Constructor ───────────────────────────────────────────────────────────

    public TestViewModel()
    {
        _p2p.StatusChanged    += OnStatus;
        _p2p.PeerConnected    += info => Dispatch(() =>
        {
            Members.Add(info.Username);
            DebugPeerCount = _p2p.PeerCount.ToString();
            AddLog($"متصل شد: {info.Username} ({info.EndPoint})");
        });
        _p2p.PeerDisconnected += (id, reason) => Dispatch(() =>
        {
            DebugPeerCount = _p2p.PeerCount.ToString();
            AddLog($"قطع شد (Peer {id}): {reason}");
        });
        _p2p.MessageReceived  += (id, frame) => Dispatch(() =>
            AddLog($"پیام از Peer {id}: [{frame.Type}] {frame.Payload.Length} bytes"));
        _p2p.ConnectionFailed += (title, msg) => Dispatch(() =>
        {
            SetStatus("خطا", "#F04747");
            AddLog($"خطا: {title}\n{msg}");
            MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public async Task CreateRoomAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        SetStatus("در حال ایجاد Room...", "#FAA61A");
        AddLog("شروع Host...");

        try
        {
            var (ok, lanCode, internetCode) = await _p2p.StartAsHostAsync(Username, port: 42777);
            if (ok)
            {
                ConnectionCode = string.IsNullOrEmpty(internetCode) ? lanCode : $"{lanCode} / {internetCode}";
                DebugRole = "Host";
                UpdateDebugFromNat();
                SetStatus("Host — منتظر اتصال", "#43B581");
                AddLog($"LAN Code: {lanCode}");
                if (!string.IsNullOrEmpty(internetCode)) AddLog($"Internet Code: {internetCode}");
            }
        }
        finally { IsBusy = false; }
    }

    public async Task JoinRoomAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(JoinCode)) return;
        IsBusy = true;
        SetStatus("در حال اتصال...", "#FAA61A");
        AddLog($"تلاش اتصال با Code: {JoinCode}");

        try
        {
            bool ok = await _p2p.ConnectAsGuestAsync(JoinCode.Trim(), Username);
            if (ok)
            {
                IsConnected = true;
                DebugRole = "Guest";
                SetStatus("متصل", "#43B581");
                AddLog("اتصال موفق!");
            }
        }
        finally { IsBusy = false; }
    }

    public void Disconnect()
    {
        _p2p.Shutdown();
        IsConnected = false;
        ConnectionCode = "";
        Members.Clear();
        SetStatus("قطع شده", "#747F8D");
        ResetDebug();
        AddLog("اتصال قطع شد.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void OnStatus(string msg) => Dispatch(() =>
    {
        StatusText = msg;
        AddLog(msg);
    });

    private void UpdateDebugFromNat()
    {
        var s = _p2p.NatStatus;
        DebugLocalIP   = s.LocalIP   ?? "—";
        DebugPublicIP  = s.PublicIP  ?? "—";
        DebugLocalPort = s.LocalPort?.ToString() ?? "—";
        DebugExtPort   = s.ExternalPort?.ToString() ?? "—";
        DebugNatType   = s.NatType;
    }

    private void ResetDebug()
    {
        DebugLocalIP = DebugPublicIP = DebugLocalPort = DebugExtPort = DebugNatType = "—";
        DebugPeerCount = "0";
        DebugRole = "—";
    }

    private void SetStatus(string text, string color)
    {
        StatusText  = text;
        StatusColor = color;
    }

    private void AddLog(string entry)
        => LogEntries.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {entry}");

    private static void Dispatch(Action a)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == true) a();
        else Application.Current?.Dispatcher.Invoke(a);
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose() => _p2p.Dispose();
}
