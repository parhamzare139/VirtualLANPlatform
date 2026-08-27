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
    private bool   _disposed;
    private IntPtr _windowHandle;

    // Guards against overlapping Capture() calls: the timer fires on a fixed period
    // regardless of how long the previous tick took, and the adaptive-quality fields
    // below are read/written with no lock — an overlap would corrupt them and, on the
    // slow-path, actively compound the very pile-up AdaptQuality exists to relieve.
    private int _capturing;

    // Adaptive quality state
    private int _jpegQuality = 75;
    private int _targetFps   = 10;
    private int _slowCount;
    private int _fastCount;

    private const int MinQuality = 30;
    private const int MaxQuality = 90;
    private const int MinFps     = 3;
    private const int MaxFps     = 15;

    public bool IsSharing { get; private set; }
    public bool IsAudioOn { get; private set; }

    public event Action<byte[]>?             FrameCaptured;
    public event Action<byte[], WaveFormat>? AudioCaptured;

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public void Start(int fps = 10, IntPtr windowHandle = default, bool shareAudio = false)
    {
        if (IsSharing) return;
        IsSharing     = true;
        _windowHandle = windowHandle;
        _jpegQuality  = 75;
        _targetFps    = Math.Clamp(fps, MinFps, MaxFps);
        _slowCount    = 0;
        _fastCount    = 0;

        _timer = new System.Threading.Timer(_ => Capture(), null, 0, 1000 / _targetFps);

        if (shareAudio) StartAudio();
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
        if (System.Threading.Interlocked.CompareExchange(ref _capturing, 1, 0) != 0) return;
        try
        {
            CaptureCore();
        }
        finally
        {
            _capturing = 0;
        }
    }

    private void CaptureCore()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Bitmap bmp;
            Rectangle screenBounds = default;
            if (_windowHandle != IntPtr.Zero)
            {
                if (!GetWindowRect(_windowHandle, out RECT rect)) return;
                int w = rect.Right  - rect.Left;
                int h = rect.Bottom - rect.Top;
                if (w <= 0 || h <= 0 || w > 8000 || h > 8000) return;
                bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            }
            else
            {
                screenBounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                bmp = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppRgb);
            }

            // bmp is wrapped in `using` immediately on allocation — GetHdc/PrintWindow/
            // ReleaseHdc and CopyFromScreen below can all throw, and this way the GDI
            // handle is still released even if one of them does, instead of leaking it
            // (repeatable, e.g. every failed capture attempt against a locked desktop).
            using (bmp)
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    if (_windowHandle != IntPtr.Zero)
                    {
                        IntPtr hdc = g.GetHdc();
                        PrintWindow(_windowHandle, hdc, 2); // PW_RENDERFULLCONTENT
                        g.ReleaseHdc(hdc);
                    }
                    else
                    {
                        g.CopyFromScreen(screenBounds.X, screenBounds.Y, 0, 0, screenBounds.Size);
                    }
                }

                // Reduce max width at lower quality to save bandwidth
                double maxW = _jpegQuality >= 65 ? 1920.0 : _jpegQuality >= 45 ? 1280.0 : 800.0;
                double scale = Math.Min(1.0, maxW / bmp.Width);
                int tw = (int)(bmp.Width  * scale);
                int th = (int)(bmp.Height * scale);

                using var thumb = new Bitmap(bmp, tw, th);
                using var ms    = new MemoryStream();
                var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)_jpegQuality);
                thumb.Save(ms, JpegCodec, ep);
                FrameCaptured?.Invoke(ms.ToArray());
            }
        }
        catch { }
        finally
        {
            sw.Stop();
            AdaptQuality(sw.ElapsedMilliseconds);
        }
    }

    // Adjust JPEG quality and FPS based on how long encoding takes relative to the frame interval.
    private void AdaptQuality(long encodeMs)
    {
        int interval = 1000 / Math.Max(1, _targetFps);

        if (encodeMs > interval * 0.75)
        {
            _fastCount = 0;
            if (++_slowCount >= 3)
            {
                _slowCount = 0;
                if (_jpegQuality > MinQuality)
                    _jpegQuality = Math.Max(MinQuality, _jpegQuality - 10);
                else if (_targetFps > MinFps)
                {
                    _targetFps = Math.Max(MinFps, _targetFps - 2);
                    _timer?.Change(0, 1000 / _targetFps);
                }
            }
        }
        else if (encodeMs < interval * 0.35)
        {
            _slowCount = 0;
            if (++_fastCount >= 10)
            {
                _fastCount = 0;
                if (_jpegQuality < MaxQuality)
                    _jpegQuality = Math.Min(MaxQuality, _jpegQuality + 5);
                else if (_targetFps < MaxFps)
                {
                    _targetFps = Math.Min(MaxFps, _targetFps + 2);
                    _timer?.Change(0, 1000 / _targetFps);
                }
            }
        }
        else
        {
            _slowCount = 0;
            _fastCount = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
