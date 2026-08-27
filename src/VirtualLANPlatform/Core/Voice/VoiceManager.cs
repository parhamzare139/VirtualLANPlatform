using Concentus.Enums;
using Concentus.Structs;
using LiteNetLib;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;

namespace VirtualLANPlatform.Core.Voice;

/// <summary>
/// Real-time voice chat over P2P.
/// Capture: WASAPI (Communications role → Windows AEC/NS/AGC APOs) → resample → Opus
/// Playback: Opus decode → gain → BufferedWaveProvider → WaveOutEvent
/// Fallback: WinMM WaveInEvent when WASAPI is unavailable.
/// Noise gate: frames with RMS below threshold are dropped before encoding.
/// </summary>
public sealed class VoiceManager : IDisposable
{
    private readonly P2PManager _p2p;

    private IWaveIn?              _waveIn;
    private BufferedWaveProvider? _captureBuffer;    // raw WASAPI frames (native format)
    private IWaveProvider?        _captureConverted; // after resample → 48 kHz mono 16-bit

    private OpusEncoder? _encoder;
    // Written from the P2P poll thread (OnPeerConnected/OnPeerDisconnected, OnP2PMessage)
    // and read from the UI thread (ToggleSpeaker) — must be thread-safe.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, OpusDecoder> _decoders = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (BufferedWaveProvider Buffer, WaveOutEvent Out)> _outputs = new();

    // Guards _waveIn/_encoder/_captureBuffer/_captureConverted/_captureTail* — these are
    // touched both from the UI thread (ToggleMic/SetForceMuted) and the P2P poll thread
    // (OnPeerConnected auto-starts capture) without this, two concurrent InitCapture()
    // calls could open two WASAPI devices and corrupt the shared capture-tail buffer.
    private readonly object _captureLock = new();

    private byte[] _captureTail    = [];
    private int    _captureTailLen;

    private bool _isMicActive;
    private bool _isSpeakerMuted;
    private bool _isForceMuted;
    private bool _disposed;

    private float _micGain     = 1.0f;
    private float _speakerGain = 1.0f;

    // Frames whose RMS is below this are silence — skip encoding to reduce network chatter.
    private const float NoiseGateRms = 150f; // ≈ −47 dB for 16-bit PCM

    private const int SampleRate   = 48000;
    private const int Channels     = 1;
    private const int FrameMs      = 20;
    private const int FrameSamples = SampleRate * FrameMs / 1000; // 960
    private const int FrameBytes   = FrameSamples * 2;
    private const int MaxBufferMs  = 110;

    public bool IsMicActive    => _isMicActive && !_isForceMuted;
    public bool IsSpeakerMuted => _isSpeakerMuted;
    public bool IsForceMuted   => _isForceMuted;

    public float MicGain
    {
        get => _micGain;
        set => _micGain = Math.Clamp(value, 0f, 2f);
    }

    public float SpeakerGain
    {
        get => _speakerGain;
        set => _speakerGain = Math.Clamp(value, 0f, 2f);
    }

    public event Action<bool>?   MicChanged;
    public event Action<bool>?   SpeakerChanged;
    public event Action<string>? StatusChanged;

    public VoiceManager(P2PManager p2p)
    {
        _p2p = p2p;
        _p2p.MessageReceived  += OnP2PMessage;
        _p2p.PeerConnected    += OnPeerConnected;
        _p2p.PeerDisconnected += OnPeerDisconnected;
    }

    // ── Mic & Speaker toggles ─────────────────────────────────────────────────

    public void ToggleMic()
    {
        if (_isForceMuted) { StatusChanged?.Invoke("میکروفون توسط میزبان قفل شده"); return; }
        _isMicActive = !_isMicActive;

        lock (_captureLock)
        {
            // Actually stop the physical device on mute — otherwise Windows' mic-in-use
            // indicator (and AEC/AGC pipeline) keeps running the whole call even while
            // the user believes the mic is off.
            if (_isMicActive) { if (_waveIn == null) InitCapture(); }
            else StopCapture();
        }

        MicChanged?.Invoke(_isMicActive);
        StatusChanged?.Invoke(_isMicActive ? "میکروفون فعال" : "میکروفون خاموش");
    }

    public void ToggleSpeaker()
    {
        _isSpeakerMuted = !_isSpeakerMuted;
        if (_isSpeakerMuted)
            foreach (var (buffer, _) in _outputs.Values)
                buffer.ClearBuffer();
        SpeakerChanged?.Invoke(!_isSpeakerMuted);
        StatusChanged?.Invoke(_isSpeakerMuted ? "اسپیکر خاموش" : "اسپیکر فعال");
    }

    public void SetForceMuted(bool muted)
    {
        if (_isForceMuted == muted) return;
        _isForceMuted = muted;

        lock (_captureLock)
        {
            if (muted) StopCapture();
            else if (_isMicActive && _waveIn == null) InitCapture();
        }

        MicChanged?.Invoke(IsMicActive);
        StatusChanged?.Invoke(muted ? "میزبان میکروفون شما را بست" : "میزبان میکروفون شما را باز کرد");
    }

    // ── Peer lifecycle ────────────────────────────────────────────────────────

    private void OnPeerConnected(PeerInfo peer)
    {
        var fmt    = new WaveFormat(SampleRate, 16, Channels);
        var buffer = new BufferedWaveProvider(fmt)
        {
            BufferDuration          = TimeSpan.FromMilliseconds(400),
            DiscardOnBufferOverflow = true
        };
        var waveOut = new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 3 };
        waveOut.Init(buffer);
        waveOut.Play();

        _outputs[peer.Id]  = (buffer, waveOut);
#pragma warning disable CS0618
        _decoders[peer.Id] = new OpusDecoder(SampleRate, Channels);
#pragma warning restore CS0618

        if (!_isMicActive)
        {
            _isMicActive = true;
            lock (_captureLock) { if (_waveIn == null) InitCapture(); }
            MicChanged?.Invoke(IsMicActive);
        }
    }

    private void OnPeerDisconnected(int peerId, string reason)
    {
        if (_outputs.TryRemove(peerId, out var pair))
        {
            try { pair.Out.Stop(); } catch { }
            pair.Out.Dispose();
        }
        _decoders.TryRemove(peerId, out _);
    }

    // ── Capture ───────────────────────────────────────────────────────────────

    private void InitCapture()
    {
        try
        {
#pragma warning disable CS0618
            _encoder = new OpusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
#pragma warning restore CS0618
            _encoder.Bitrate    = 48000; // higher fidelity than 32 kbps
            _encoder.Complexity = 8;     // near-max quality, tolerable CPU
            _encoder.UseVBR     = true;
            try { _encoder.PacketLossPercent = 10; } catch { }

            // WASAPI Communications role → activates Windows AEC / Noise Suppression / AGC APOs
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var commsDev   = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                var wasapi     = new WasapiCapture(commsDev, false, FrameMs);

                WaveFormat nativeFmt = wasapi.WaveFormat;
                _captureBuffer = new BufferedWaveProvider(nativeFmt) { DiscardOnBufferOverflow = true };

                // Conversion chain: native format → 48 kHz mono 16-bit PCM for Opus
                ISampleProvider sp = _captureBuffer.ToSampleProvider();
                if (nativeFmt.Channels > 1) sp = sp.ToMono();
                if (nativeFmt.SampleRate != SampleRate) sp = new WdlResamplingSampleProvider(sp, SampleRate);
                _captureConverted = new SampleToWaveProvider16(sp);

                wasapi.DataAvailable += OnWasapiDataAvailable;
                wasapi.StartRecording();
                _waveIn = wasapi;
                StatusChanged?.Invoke("میکروفون فعال");
            }
            catch
            {
                // Fallback to legacy WinMM (no system-level AEC)
                _captureBuffer    = null;
                _captureConverted = null;
                var waveIn = new WaveInEvent
                {
                    WaveFormat         = new WaveFormat(SampleRate, 16, Channels),
                    BufferMilliseconds = FrameMs,
                    NumberOfBuffers    = 3
                };
                waveIn.DataAvailable += OnDataAvailable;
                waveIn.StartRecording();
                _waveIn = waveIn;
                StatusChanged?.Invoke("میکروفون فعال");
            }
        }
        catch (Exception ex)
        {
            _isMicActive = false;
            MicChanged?.Invoke(false);
            StatusChanged?.Invoke($"میکروفون در دسترس نیست: {ex.Message}");
        }
    }

    /// <summary>Must be called under <see cref="_captureLock"/>. Stops and releases the
    /// physical capture device so mute actually mutes it (mic-in-use indicator, AEC/AGC),
    /// instead of just discarding frames while the device keeps recording.</summary>
    private void StopCapture()
    {
        if (_waveIn == null) return;
        try { _waveIn.StopRecording(); } catch { }
        try { _waveIn.Dispose(); } catch { }
        _waveIn           = null;
        _captureBuffer    = null;
        _captureConverted = null;
        _captureTailLen   = 0;
        _encoder          = null;
    }

    // WASAPI path: native format → conversion chain → frame accumulator → encode
    //
    // This callback runs on NAudio's own capture thread, which never takes
    // _captureLock. StopCapture() (always called under _captureLock from the UI
    // thread via ToggleMic/SetForceMuted, or from Reset/Dispose) nulls _encoder/
    // _captureBuffer/_captureConverted — snapshot each field once into a local so
    // a StopCapture() landing mid-callback can't null it out between a check and
    // a use, which would otherwise NullReferenceException on this thread and,
    // uncaught, take the whole process down.
    private void OnWasapiDataAvailable(object? sender, WaveInEventArgs e)
    {
        var encoder   = _encoder;
        var buffer    = _captureBuffer;
        var converted = _captureConverted;
        if (!IsMicActive || encoder == null || !_p2p.IsRunning) return;
        if (buffer == null || converted == null) return;

        buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        var readBuf = new byte[FrameBytes * 4];
        int read = converted.Read(readBuf, 0, readBuf.Length);
        if (read <= 0) return;

        int newLen = _captureTailLen + read;
        if (_captureTail.Length < newLen) Array.Resize(ref _captureTail, Math.Max(newLen, FrameBytes * 4));
        Buffer.BlockCopy(readBuf, 0, _captureTail, _captureTailLen, read);
        _captureTailLen = newLen;
        EncodeAndSend(encoder);
    }

    // WinMM path: already 48 kHz mono 16-bit, accumulate directly
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var encoder = _encoder;
        if (!IsMicActive || encoder == null || !_p2p.IsRunning) { _captureTailLen = 0; return; }

        int newLen = _captureTailLen + e.BytesRecorded;
        if (_captureTail.Length < newLen) Array.Resize(ref _captureTail, Math.Max(newLen, FrameBytes * 4));
        Buffer.BlockCopy(e.Buffer, 0, _captureTail, _captureTailLen, e.BytesRecorded);
        _captureTailLen = newLen;
        EncodeAndSend(encoder);
    }

    private void EncodeAndSend(OpusEncoder encoder)
    {
        int offset  = 0;
        var pcm     = new short[FrameSamples];
        var encoded = new byte[1275];

        while (_captureTailLen - offset >= FrameBytes)
        {
            Buffer.BlockCopy(_captureTail, offset, pcm, 0, FrameBytes);
            offset += FrameBytes;

            if (RmsOf(pcm, FrameSamples) < NoiseGateRms) continue; // noise gate

            ApplyGain(pcm, pcm.Length, _micGain);

            try
            {
#pragma warning disable CS0618
                int len = encoder.Encode(pcm, 0, FrameSamples, encoded, 0, encoded.Length);
#pragma warning restore CS0618
                if (len > 0)
                    _p2p.SendToAll(MessageType.VoiceData, encoded[..len], DeliveryMethod.Unreliable);
            }
            catch { }
        }

        _captureTailLen -= offset;
        if (_captureTailLen > 0)
            Buffer.BlockCopy(_captureTail, offset, _captureTail, 0, _captureTailLen);
    }

    // ── Playback ──────────────────────────────────────────────────────────────

    private void OnP2PMessage(int peerId, MessageFrame frame)
    {
        if (frame.Type != MessageType.VoiceData) return;
        if (_isSpeakerMuted) return;
        if (!_outputs.TryGetValue(peerId, out var pair)) return;
        if (!_decoders.TryGetValue(peerId, out var decoder)) return;

        try
        {
            byte[]  enc     = frame.Payload.ToArray();
            short[] decoded = new short[FrameSamples];
#pragma warning disable CS0618
            int frames = decoder.Decode(enc, 0, enc.Length, decoded, 0, FrameSamples, false);
#pragma warning restore CS0618
            if (frames <= 0) return;

            ApplyGain(decoded, frames, _speakerGain);

            if (pair.Buffer.BufferedDuration.TotalMilliseconds > MaxBufferMs)
                pair.Buffer.ClearBuffer();

            byte[] pcmBytes = new byte[frames * 2];
            Buffer.BlockCopy(decoded, 0, pcmBytes, 0, pcmBytes.Length);
            pair.Buffer.AddSamples(pcmBytes, 0, pcmBytes.Length);
        }
        catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static float RmsOf(short[] samples, int count)
    {
        long sum = 0;
        for (int i = 0; i < count; i++) sum += (long)samples[i] * samples[i];
        return count > 0 ? (float)Math.Sqrt((double)sum / count) : 0f;
    }

    private static void ApplyGain(short[] samples, int count, float gain)
    {
        if (Math.Abs(gain - 1.0f) < 0.01f) return;
        for (int i = 0; i < count; i++)
        {
            int v = (int)(samples[i] * gain);
            samples[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        }
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    public void Reset()
    {
        _isMicActive    = false;
        _isSpeakerMuted = false;
        _isForceMuted   = false;
        lock (_captureLock) { StopCapture(); }

        // Release the previous room's per-peer playback devices — otherwise every
        // join/leave cycle leaks a WaveOutEvent (open WASAPI render handle) per peer.
        foreach (var (_, (_, waveOut)) in _outputs)
        {
            try { waveOut.Stop(); } catch { }
            waveOut.Dispose();
        }
        _outputs.Clear();
        _decoders.Clear();

        MicChanged?.Invoke(false);
        SpeakerChanged?.Invoke(true);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _p2p.MessageReceived  -= OnP2PMessage;
        _p2p.PeerConnected    -= OnPeerConnected;
        _p2p.PeerDisconnected -= OnPeerDisconnected;

        lock (_captureLock) { StopCapture(); }

        foreach (var (_, (_, waveOut)) in _outputs)
        {
            try { waveOut.Stop(); } catch { }
            waveOut.Dispose();
        }
        _outputs.Clear();
        _decoders.Clear();
    }
}
