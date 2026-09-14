using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace VirtualLANPlatform.UI.Views;

public partial class ScreenShareViewer : Window
{
    public ScreenShareViewer()
    {
        InitializeComponent();
    }

    public void SetSharer(string username)
    {
        SharingLabel.Text = Localization.Loc.T("Sv_ViewerTitle", username);
        Title = Localization.Loc.T("Sv_SharingTitle", username);
    }

    /// <summary>Called from any thread; dispatches to UI thread.</summary>
    public void ShowFrame(byte[] jpegBytes)
    {
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption  = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(jpegBytes);
                bmp.EndInit();
                bmp.Freeze();
                FrameImage.Source = bmp;
            }
            catch { }
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
