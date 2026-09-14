using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color   = System.Windows.Media.Color;
using Colors  = System.Windows.Media.Colors;
using Cursors = System.Windows.Input.Cursors;

namespace VirtualLANPlatform.UI.Views;

public partial class WindowPickerDialog : Window
{
    // ── Win32 ──────────────────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int  GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // ── Result ─────────────────────────────────────────────────────────────────

    public IntPtr SelectedHandle { get; private set; } = IntPtr.Zero; // zero = full screen
    public bool   ShareAudio     => AudioCheck.IsChecked == true;

    // ── State ──────────────────────────────────────────────────────────────────

    private Border? _selected;

    private static readonly Color SelectedBg   = Color.FromRgb(0x28, 0x38, 0x8F);
    private static readonly Color SelectedBdr  = Color.FromRgb(0x43, 0x61, 0xEE);
    private static readonly Color DefaultBg    = Color.FromRgb(0x1E, 0x1F, 0x22);
    private static readonly Color HoverBg      = Color.FromRgb(0x31, 0x33, 0x38);

    public WindowPickerDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadWindowsAsync();
    }

    // ── Window enumeration ─────────────────────────────────────────────────────

    private async Task LoadWindowsAsync()
    {
        // Full-screen card first — always available
        var fullCard = BuildCard(IntPtr.Zero, Localization.Loc.T("Wp_FullScreen"), isFullScreen: true, thumb: null);
        WindowsPanel.Children.Add(fullCard);
        SelectCard(fullCard, IntPtr.Zero);

        // Enumerate visible windows on a background thread (fast — no GDI)
        var handles = await Task.Run(() =>
        {
            var list = new List<(IntPtr Handle, string Title)>();
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                var sb  = new StringBuilder(256);
                int len = GetWindowText(hwnd, sb, 256);
                if (len > 2)
                    list.Add((hwnd, sb.ToString().Trim()));
                return true;
            }, IntPtr.Zero);
            return list;
        });

        // Add cards with placeholder thumbnails, then load thumbnails in background
        var cards = new List<(Border Card, IntPtr Handle)>();
        foreach (var (hwnd, title) in handles.Take(24))
        {
            var card = BuildCard(hwnd, title, isFullScreen: false, thumb: null);
            WindowsPanel.Children.Add(card);
            cards.Add((card, hwnd));
        }

        // Capture thumbnails one by one without blocking the UI
        foreach (var (card, hwnd) in cards)
        {
            var thumb = await Task.Run(() => CaptureThumb(hwnd));
            if (thumb != null && card.Tag is System.Windows.Controls.Image img)
                img.Source = thumb;
        }
    }

    // ── Card builder ───────────────────────────────────────────────────────────

    private Border BuildCard(IntPtr hwnd, string title, bool isFullScreen, ImageSource? thumb)
    {
        const double W = 162;
        const double H = 128;

        var imgCtrl = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Uniform,
            Margin  = new Thickness(4, 6, 4, 2),
            Source  = thumb
        };

        var placeholder = new TextBlock
        {
            Text                = "🖥",
            FontSize            = 34,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Foreground          = new SolidColorBrush(Color.FromRgb(0x72, 0x76, 0x7D))
        };

        var topLayer = new Grid();
        topLayer.Children.Add(placeholder);
        topLayer.Children.Add(imgCtrl);

        var label = new TextBlock
        {
            Text          = title.Length > 22 ? title[..22] + "…" : title,
            FontSize      = 11,
            TextTrimming  = TextTrimming.CharacterEllipsis,
            Foreground    = new SolidColorBrush(Color.FromRgb(0xDB, 0xDE, 0xE1)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin        = new Thickness(4, 0, 4, 6),
            MaxWidth      = W - 8
        };

        var stack = new StackPanel();
        var imgRow = new Border { Height = H - 30 };
        imgRow.Child = topLayer;
        stack.Children.Add(imgRow);
        stack.Children.Add(label);

        var card = new Border
        {
            Width           = W,
            Height          = H,
            Margin          = new Thickness(5),
            CornerRadius    = new CornerRadius(8),
            Background      = new SolidColorBrush(DefaultBg),
            BorderBrush     = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(2),
            Cursor          = Cursors.Hand,
            Tag             = imgCtrl,     // used by thumbnail loader
            Child           = stack
        };

        card.MouseEnter        += (_, _) => { if (card != _selected) card.Background = new SolidColorBrush(HoverBg); };
        card.MouseLeave        += (_, _) => { if (card != _selected) card.Background = new SolidColorBrush(DefaultBg); };
        card.MouseLeftButtonUp += (_, _) => SelectCard(card, hwnd);

        return card;
    }

    private void SelectCard(Border card, IntPtr hwnd)
    {
        if (_selected != null)
        {
            _selected.Background  = new SolidColorBrush(DefaultBg);
            _selected.BorderBrush = new SolidColorBrush(Colors.Transparent);
        }
        _selected      = card;
        SelectedHandle = hwnd;
        card.Background  = new SolidColorBrush(SelectedBg);
        card.BorderBrush = new SolidColorBrush(SelectedBdr);
    }

    // ── Thumbnail capture ──────────────────────────────────────────────────────

    private static ImageSource? CaptureThumb(IntPtr hwnd)
    {
        try
        {
            if (!GetWindowRect(hwnd, out RECT r)) return null;
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0 || w > 8000 || h > 8000) return null;

            using var bmp = new System.Drawing.Bitmap(w, h,
                System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                PrintWindow(hwnd, hdc, 2); // PW_RENDERFULLCONTENT
                g.ReleaseHdc(hdc);
            }

            double scale = Math.Min(1.0, 150.0 / Math.Max(w, 1));
            int tw = (int)(w * scale);
            int th = (int)(h * scale);
            using var thumb = new System.Drawing.Bitmap(bmp, tw, th);
            using var ms    = new System.IO.MemoryStream();
            thumb.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption  = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    // ── Button handlers ────────────────────────────────────────────────────────

    private void Ok_Click(object sender, RoutedEventArgs e)     => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // Allow dragging the borderless window
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        try { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); } catch { }
    }
}
