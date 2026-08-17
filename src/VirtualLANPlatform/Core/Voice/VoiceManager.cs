using Concentus.Enums;
using Concentus.Structs;
using LiteNetLib;
using NAudio.Wave;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;

namespace VirtualLANPlatform.Core.Voice;

/// <summary>
/// Phase 7 — real-time voice chat over P2P.
/// Mic:     WaveInEvent → gain → Opus encode → P2P (Unreliable UDP)
/// Speaker: P2P receive → Opus decode → gain → BufferedWaveProvider
///
/// Latency budget (target ≈ 75 ms one-way, excluding network):
///   capture 20 ms · encode ~1 ms · playback buffer 20 ms · WaveOut 40 ms
/// A jitter watchdog trims the playback buffer whenever it drifts past
/// <see cref="MaxBufferMs"/>, so latency cannot creep upward over a long call.
/// </summary>
public sealed class VoiceManager : IDisposable
{
    private readonly P2PManager _p2p;

    private WaveInEvent? _waveIn;
    private OpusEncoder? _encoder;
    private readonly Dictionary<int, OpusDecoder> _decoders = [];
    private readonly Dictionary<int, (BufferedWaveProvider Buffer, WaveOutEvent Out)> _outputs = [];

    /// <summary>Leftover capture bytes that didn't fill a whole 20 ms frame.</summary>
    private byte[] _captureTail   = [];
    private int    _captureTailLen;

    private bool _isMicActive;      // true = sending audio to peers
    private bool _isSpeakerMuted;   // true = not playing received audio
    private bool _isForceMuted;     // true = host has muted us; overrides _isMicActive
    private bool _disposed;

    private float _micGain     = 1.0f;
    private float _speakerGain = 1.0f;

    private const int SampleRate   = 48000;
    private const int Channels     = 1;
    private const int FrameMs      = 20;
    private const int FrameSamples = SampleRate * FrameMs / 1000; // 960 samples
    private const int FrameBytes   = FrameSamples * 2;

    /// <summary>Playback backlog above this is dropped — it is pure added latency.</summary>
    private const int MaxBufferMs = 110;

    public bool IsMicActive    => _isMicActive && !_isForceMuted;
    public bool IsSpeakerMuted => _isSpeakerMuted;
    public bool IsForceMuted   => _isForceMuted;

    /// <summary>Microphone gain, 0.0 – 2.0 (1.0 = unchanged).</summary>
    public float MicGain
    {
        get => _micGain;
        set => _micGain = Math.Clamp(value, 0f, 2f);
    }

    /// <summary>Speaker gain, 0.0 – 2.0 (1.0 = unchanged).</summary>
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
        if (_isForceMuted)
        {
            StatusChanged?.Invoke("میکروفون توسط میزبان قفل شده");
            return;
        }

        _isMicActive = !_isMicActive;

        if (_isMicActive && _waveIn == null)
            InitCapture();

        MicChanged?.Invoke(_isMicActive);
        StatusChanged?.Invoke(_isMicActive ? "میکروفون فعال" : "میکروفون خاموش");
    }

    public void ToggleSpeaker()
    {
        _isSpeakerMuted = !_isSpeakerMuted;

        // Drop whatever is queued so unmuting resumes at live position, not stale audio.
        if (_isSpeakerMuted)
            foreach (var (buffer, _) in _outputs.Values)
                buffer.ClearBuffer();

        SpeakerChanged?.Invoke(!_isSpeakerMuted);
        StatusChanged?.Invoke(_isSpeakerMuted ? "اسپیکر خاموش" : "اسپیکر فعال");
    }

    /// <summary>Applied when the host issues a mute command — the user cannot undo it.</summary>
    public void SetForceMuted(bool muted)
    {
        if (_isForceMuted == muted) return;
        _isForceMuted = muted;

        MicChanged?.Invoke(IsMicActive);
        StatusChanged?.Invoke(muted
            ? "میزبان میکروفون شما را بست"
            : "میزبان میکروفون شما را باز کرد");
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

        // 3 buffers × 20 ms — the lowest WaveOut runs at without crackling.
        var waveOut = new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 3 };
        waveOut.Init(buffer);
        waveOut.Play();

        _outputs[peer.Id]  = (buffer, waveOut);
#pragma warning disable CS0618
        _decoders[peer.Id] = new OpusDecoder(SampleRate, Channels);
#pragma warning restore CS0618

        // Auto-enable mic on first connection
        if (!_isMicActive)
        {
            _isMicActive = true;
            if (_waveIn == null) InitCapture();
            MicChanged?.Invoke(IsMicActive);
        }
    }

    private void OnPeerDisconnected(int peerId, string _)
    {
        if (_outputs.TryGetValue(peerId, out var pair))
        {
            try { pair.Out.Stop(); } catch { }
            pair.Out.Dispose();
            _outputs.Remove(peerId);
        }
        _decoders.Remove(peerId);
    }

    // ── Capture ───────────────────────────────────────────────────────────────

    private void InitCapture()
    {
        try
        {
#pragma warning disable CS0618
            _encoder = new OpusEncoder(SampleRate, Channels,
                OpusApplication.OPUS_APPLICATION_VOIP);
#pragma warning restore CS0618
            _encoder.Bitrate    = 32000;
            _encoder.Complexity = 5;     // half the CPU of the default 10, no audible loss at 32 kbps
            _encoder.UseVBR     = true;

            _waveIn = new WaveInEvent
            {
                WaveFormat         = new WaveFormat(SampleRate, 16, Channels),
                BufferMilliseconds = FrameMs,
                NumberOfBuffers    = 3
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
            StatusChanged?.Invoke("میکروفون فعال");
        }
        catch (Exception ex)
        {
            _isMicActive = false;
            MicChanged?.Invoke(false);
            StatusChanged?.Invoke($"میکروفون در دسترس نیست: {ex.Message}");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!IsMicActive || _encoder == null || !_p2p.IsRunning)
        {
            _captureTailLen = 0;
            return;
        }

        // Join the previous partial frame with this callback's bytes, then emit
        // every whole 20 ms frame. Anything left over waits for the next callback —
        // dropping it (as the old code did) punched a hole in the audio.
        int total = _captureTailLen + e.BytesRecorded;
        if (_captureTail.Length < total)
            Array.Resize(ref _captureTail, Math.Max(total, FrameBytes * 4));

        Buffer.BlockCopy(e.Buffer, 0, _captureTail, _captureTailLen, e.BytesRecorded);

        int offset = 0;
        var pcm    = new short[FrameSamples];
        var encoded = new byte[1275];

        while (total - offset >= FrameBytes)
        {
            Buffer.BlockCopy(_captureTail, offset, pcm, 0, FrameBytes);
            offset += FrameBytes;

            ApplyGain(pcm, pcm.Length, _micGain);

            try
            {
#pragma warning disable CS0618
                int len = _encoder.Encode(pcm, 0, FrameSamples, encoded, 0, encoded.Length);
#pragma warning restore CS0618
                if (len > 0)
                    _p2p.SendToAll(MessageType.VoiceData, encoded[..len], DeliveryMethod.Unreliable);
            }
            catch { }
        }

        _captureTailLen = total - offset;
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
            byte[]  encoded = frame.Payload.ToArray();
            short[] decoded = new short[FrameSamples];
#pragma warning disable CS0618
            int frames = decoder.Decode(
                encoded, 0, encoded.Length,
                decoded, 0, FrameSamples, false);
#pragma warning restore CS0618
            if (frames <= 0) return;

            ApplyGain(decoded, frames, _speakerGain);

            // Jitter watchdog: a backlog is latency the listener can hear. If the
            // sender ran ahead (or we stalled), throw the backlog away and resync.
            if (pair.Buffer.BufferedDuration.TotalMilliseconds > MaxBufferMs)
                pair.Buffer.ClearBuffer();

            byte[] pcmBytes = new byte[frames * 2];
            Buffer.BlockCopy(decoded, 0, pcmBytes, 0, pcmBytes.Length);
            pair.Buffer.AddSamples(pcmBytes, 0, pcmBytes.Length);
        }
        catch { }
    }

    /// <summary>Scales <paramref name="count"/> samples in place, saturating at the 16-bit rails.</summary>
    private static void ApplyGain(short[] samples, int count, float gain)
    {
        if (Math.Abs(gain - 1.0f) < 0.01f) return;

        for (int i = 0; i < count; i++)
        {
            int v = (int)(samples[i] * gain);
            samples[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        }
    }

    // ── Reset (called on room close/leave) ────────────────────────────────────

    public void Reset()
    {
        _isMicActive    = false;
        _isSpeakerMuted = false;
        _isForceMuted   = false;
        _captureTailLen = 0;
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

        try { _waveIn?.StopRecording(); } catch { }
        _waveIn?.Dispose();

        foreach (var (_, (_, waveOut)) in _outputs)
        {
            try { waveOut.Stop(); } catch { }
            waveOut.Dispose();
        }
        _outputs.Clear();
        _decoders.Clear();
    }
}
