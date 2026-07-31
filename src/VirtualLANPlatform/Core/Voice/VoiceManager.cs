using Concentus.Enums;
using Concentus.Structs;
using LiteNetLib;
using NAudio.Wave;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;

namespace VirtualLANPlatform.Core.Voice;

/// <summary>
/// Phase 7 — real-time voice chat over P2P.
/// Mic:     WaveInEvent → Opus encode → P2P (Unreliable UDP)  — toggled by ToggleMic()
/// Speaker: P2P receive → Opus decode → BufferedWaveProvider  — toggled by ToggleSpeaker()
/// Both are ON by default when a peer connects.
/// </summary>
public sealed class VoiceManager : IDisposable
{
    private readonly P2PManager _p2p;

    private WaveInEvent? _waveIn;
    private OpusEncoder? _encoder;
    private readonly Dictionary<int, OpusDecoder>                              _decoders = [];
    private readonly Dictionary<int, (BufferedWaveProvider Buffer, WaveOutEvent Out)> _outputs  = [];

    private bool _isMicActive;      // true = sending audio to peers
    private bool _isSpeakerMuted;   // true = not playing received audio
    private bool _disposed;

    private const int SampleRate   = 48000;
    private const int Channels     = 1;
    private const int FrameMs      = 20;
    private const int FrameSamples = SampleRate * FrameMs / 1000; // 960 samples

    public bool IsMicActive     => _isMicActive;
    public bool IsSpeakerMuted  => _isSpeakerMuted;

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
        _isMicActive = !_isMicActive;

        if (_isMicActive && _waveIn == null)
            InitCapture();

        MicChanged?.Invoke(_isMicActive);
        StatusChanged?.Invoke(_isMicActive ? "میکروفون فعال" : "میکروفون خاموش");
    }

    public void ToggleSpeaker()
    {
        _isSpeakerMuted = !_isSpeakerMuted;
        SpeakerChanged?.Invoke(!_isSpeakerMuted);
        StatusChanged?.Invoke(_isSpeakerMuted ? "اسپیکر خاموش" : "اسپیکر فعال");
    }

    // ── Peer lifecycle ────────────────────────────────────────────────────────

    private void OnPeerConnected(PeerInfo peer)
    {
        var fmt    = new WaveFormat(SampleRate, 16, Channels);
        var buffer = new BufferedWaveProvider(fmt)
        {
            BufferDuration        = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true
        };
        var waveOut = new WaveOutEvent();
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
            MicChanged?.Invoke(true);
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
            _encoder.Bitrate = 32000;

            _waveIn = new WaveInEvent
            {
                WaveFormat         = new WaveFormat(SampleRate, 16, Channels),
                BufferMilliseconds = FrameMs
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
        if (!_isMicActive || _encoder == null || !_p2p.IsRunning) return;
        if (e.BytesRecorded < FrameSamples * 2) return;

        short[] pcm = new short[FrameSamples];
        Buffer.BlockCopy(e.Buffer, 0, pcm, 0, FrameSamples * 2);

        try
        {
            byte[] encoded = new byte[1275];
#pragma warning disable CS0618
            int len = _encoder.Encode(pcm, 0, FrameSamples, encoded, 0, encoded.Length);
#pragma warning restore CS0618
            if (len <= 0) return;
            _p2p.SendToAll(MessageType.VoiceData, encoded[..len], DeliveryMethod.Unreliable);
        }
        catch { }
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

            byte[] pcmBytes = new byte[frames * 2];
            Buffer.BlockCopy(decoded, 0, pcmBytes, 0, pcmBytes.Length);
            pair.Buffer.AddSamples(pcmBytes, 0, pcmBytes.Length);
        }
        catch { }
    }

    // ── Reset (called on room close/leave) ────────────────────────────────────

    public void Reset()
    {
        _isMicActive    = false;
        _isSpeakerMuted = false;
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
