using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace VirtualLANPlatform.Core.ScreenShare;

public sealed class ScreenShareManager : IDisposable
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private System.Threading.Timer? _timer;
    private WasapiLoopbackCapture?  _audioCapture;
    private bool _disposed;
    private IntPtr _windowHandle;

    public bool IsSharing   { get; private set; }
    public bool IsAudioOn   { get; private set; }

    public event Action<byte[]>?                 FrameCaptured;
    public event Action<byte[], WaveFormat>?     AudioCaptured;

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public void Start(int fps = 8, IntPtr windowHandle = default, bool shareAudio = false)
    {
        if (IsSharing) return;
        IsSharing     = true;
        _windowHandle = windowHandle;

        int interval = 1000 / fps;
        _timer = new System.Threading.Timer(_ => Capture(), null, 0, interval);

        if (shareAudio)
            StartAudio();
    }

    public void Stop()
    {
        IsSharing = false;
        IsAudioOn = false;
        _timer?.Dispose();
        _timer = null;
        _audioCapture?.StopRecording();
        _audioCapture?.Dispose();
        _audioCapture = null;
    }

    private void StartAudio()
    {
        try
        {
            _audioCapture = new WasapiLoopbackCapture();
            var fmt = _audioCapture.WaveFormat;
            _audioCapture.DataAvailable += (_, e) =>
            {
                if (!IsSharing || e.BytesRecorded == 0) return;
                var data = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, data, e.BytesRecorded);
                AudioCaptured?.Invoke(data, fmt);
            };
            _audioCapture.StartRecording();
            IsAudioOn = true;
        }
        catch { }
    }

    private void Capture()
    {
        if (!IsSharing) return;
        try
        {
            Bitmap bmp;
            if (_windowHandle != IntPtr.Zero)
            {
                if (!GetWindowRect(_windowHandle, out RECT rect)) return;
                int w = rect.Right  - rect.Left;
                int h = rect.Bottom - rect.Top;
                if (w <= 0 || h <= 0 || w > 8000 || h > 8000) return;

                bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    PrintWindow(_windowHandle, hdc, 2); // PW_RENDERFULLCONTENT
                    g.ReleaseHdc(hdc);
                }
            }
            else
            {
                var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
            }

            using (bmp)
            {
                double scale = Math.Min(1.0, 1920.0 / bmp.Width);
                int tw = (int)(bmp.Width  * scale);
                int th = (int)(bmp.Height * scale);

                using var thumb = new Bitmap(bmp, tw, th);
                using var ms    = new MemoryStream();
                var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, 70L);
                thumb.Save(ms, JpegCodec, ep);
                FrameCaptured?.Invoke(ms.ToArray());
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
