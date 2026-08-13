using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VirtualLANPlatform.UI.Views;

public sealed class ScreenShareWindow : Window
{
    private readonly System.Windows.Controls.Image _frame = new() { Stretch = Stretch.Uniform };
    private readonly System.Windows.Controls.Primitives.StatusBar _bar = new();
    private readonly TextBlock _label = new() { Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDB, 0xDE, 0xE1)), FontSize = 12 };

    public ScreenShareWindow(string sharerName)
    {
        Title      = $"اشتراک صفحه — {sharerName}";
        Background = new SolidColorBrush(Colors.Black);
        Width  = 960;
        Height = 580;
        MinWidth  = 480;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_frame, 0);
        _label.Text = $"📡  در حال دریافت صفحه‌نمایش  {sharerName}";
        _bar.Items.Add(_label);
        _bar.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x20, 0x25));
        Grid.SetRow(_bar, 1);

        grid.Children.Add(_frame);
        grid.Children.Add(_bar);
        Content = grid;
    }

    public void UpdateFrame(BitmapSource bmp)
    {
        _frame.Source = bmp;
    }

    public void SetStopped()
    {
        _label.Text = "اشتراک صفحه متوقف شد";
        _label.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFA, 0xA6, 0x1A));
    }
}
