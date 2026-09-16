using Concentus.Enums;
using Concentus.Structs;
using LiteNetLib;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;
using VirtualLANPlatform.Core.Services;

using VirtualLANPlatform.UI.Localization;

namespace VirtualLANPlatform.Core.Voice;

/// <summary>An audio endpoint as shown in the settings page.</summary>
public sealed record AudioDevice(string? Id, string Name);

/// <summary>
/// Real-time voice chat over P2P.
/// Capture: WASAPI (Communications role → Windows AEC/NS/AGC APOs) → resample → Opus
/// Playback: Opus decode → gain → BufferedWaveProvider → WaveOutEvent (or WasapiOut when
///           the user picked a specific output device)
/// Fallback: WinMM WaveInEvent when WASAPI is unavailable.
/// Noise gate: frames with RMS below threshold are dropped before encoding.
/// Push-to-talk: capture keeps running, frames are simply not sent until the key is held —
///           so the first syllable is never clipped by a device spin-up.
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (BufferedWaveProvider Buffer, IWavePlayer Out)> _outputs = new();

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
    private bool _monitorOnly;    // capture running for the settings meter, nothing is sent
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

    /// <summary>Send only while <see cref="TalkKeyDown"/>; capture stays warm regardless.</summary>
    public bool PushToTalk  { get; set; }
    public bool TalkKeyDown { get; set; }

    /// <summary>Microphone level of the last frame, 0..1 on a perceptual (dB) scale.</summary>
    public float InputLevel { get; private set; }

    /// <summary>True while frames are actually leaving the machine (mic on, above the gate,
    /// and the talk key held when push-to-talk is on). Drives the "speaking" glow.</summary>
    public bool IsTransmitting => (DateTime.UtcNow - _lastSent).TotalMilliseconds < 250;
    private DateTime _lastSent = DateTime.MinValue;

    /// <summary>WASAPI endpoint ids; null means the Windows default (communications role).</summary>
    public string? InputDeviceId  { get; set; }
    public string? OutputDeviceId { get; set; }

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

        var s = AppSettings.I;
        InputDeviceId  = s.InputDeviceId;
        OutputDeviceId = s.OutputDeviceId;
        PushToTalk     = s.PushToTalk;
    }

    // ── Devices ───────────────────────────────────────────────────────────────

    public static List<AudioDevice> ListInputs()  => List(DataFlow.Capture);
    public static List<AudioDevice> ListOutputs() => List(DataFlow.Render);

    private static List<AudioDevice> List(DataFlow flow)
    {
        var list = new List<AudioDevice>();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try { list.Add(new AudioDevice(d.ID, d.FriendlyName)); } catch { }
                d.Dispose();
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Re-opens the capture device (if running) and every playback stream so a device
    /// picked on the settings page takes effect immediately, mid-call included.
    /// </summary>
    public void ApplyDevices()
    {
        lock (_captureLock)
        {
            bool wasRunning = _waveIn != null;
            StopCapture();
            if (wasRunning) InitCapture();
        }

        foreach (var (peerId, pair) in _outputs.ToArray())
        {
            try { pair.Out.Stop(); } catch { }
            try { pair.Out.Dispose(); } catch { }
            _outputs[peerId] = CreateOutput();
        }
    }

    private (BufferedWaveProvider, IWavePlayer) CreateOutput()
    {
        var fmt    = new WaveFormat(SampleRate, 16, Channels);
        var buffer = new BufferedWaveProvider(fmt)
        {
            BufferDuration          = TimeSpan.FromMilliseconds(400),
            DiscardOnBufferOverflow = true
        };

        IWavePlayer? player = null;
        if (OutputDeviceId is { Length: > 0 } id)
        {
            try
            {
                using var e = new MMDeviceEnumerator();
                var dev = e.GetDevice(id);
                if (dev.State == DeviceState.Active)
                {
                    var wasapi = new WasapiOut(dev, AudioClientShareMode.Shared, true, 60);
                    wasapi.Init(buffer);
                    player = wasapi;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("voice", $"output device {id} unavailable, falling back: {ex.Message}");
                player = null;
            }
        }
        if (player == null)
        {
            var wo = new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 3 };
            wo.Init(buffer);
            player = wo;
        }
        player.Play();
        return (buffer, player);
    }

    // ── Mic & Speaker toggles ─────────────────────────────────────────────────

    public void ToggleMic()
    {
        if (_isForceMuted) { StatusChanged?.Invoke(Loc.T("Vc_MicLocked")); return; }
        _isMicActive = !_isMicActive;

        lock (_captureLock)
        {
            // Actually stop the physical device on mute — otherwise Windows' mic-in-use
            // indicator (and AEC/AGC pipeline) keeps running the whole call even while
            // the user believes the mic is off.
            if (_isMicActive) { if (_waveIn == null) InitCapture(); }
            else if (!_monitorOnly) StopCapture();
        }

        MicChanged?.Invoke(_isMicActive);
        StatusChanged?.Invoke(_isMicActive ? Loc.T("Vc_MicOn") : Loc.T("Vc_MicOff"));
    }

    public void ToggleSpeaker()
    {
        _isSpeakerMuted = !_isSpeakerMuted;
        if (_isSpeakerMuted)
            foreach (var (buffer, _) in _outputs.Values)
                buffer.ClearBuffer();
        SpeakerChanged?.Invoke(!_isSpeakerMuted);
        StatusChanged?.Invoke(_isSpeakerMuted ? Loc.T("Vc_SpkOff") : Loc.T("Vc_SpkOn"));
    }

    public void SetForceMuted(bool muted)
    {
        if (_isForceMuted == muted) return;
        _isForceMuted = muted;

        lock (_captureLock)
        {
            if (muted) { if (!_monitorOnly) StopCapture(); }
            else if (_isMicActive && _waveIn == null) InitCapture();
        }

        MicChanged?.Invoke(IsMicActive);
        StatusChanged?.Invoke(muted ? Loc.T("Vc_HostMuted") : Loc.T("Vc_HostUnmuted"));
    }

    // ── Mic monitor (settings page) ──────────────────────────────────────────

    /// <summary>Runs the capture chain purely to feed <see cref="InputLevel"/>. Safe to
    /// call while in a call — capture is shared and nothing extra is sent.</summary>
    public void StartMonitor()
    {
        _monitorOnly = true;
        lock (_captureLock) { if (_waveIn == null) InitCapture(); }
    }

    public void StopMonitor()
    {
        _monitorOnly = false;
        lock (_captureLock)
        {
            // Only tear the device down if the call did not want it anyway.
            if (!IsMicActive || !_p2p.IsRunning) StopCapture();
        }
        InputLevel = 0;
    }

    // ── Peer lifecycle ────────────────────────────────────────────────────────

    private void OnPeerConnected(PeerInfo peer)
    {
        EnsureOutput(peer.Id);

        if (!_isMicActive)
        {
            _isMicActive = true;
            lock (_captureLock) { if (_waveIn == null) InitCapture(); }
            MicChanged?.Invoke(IsMicActive);
        }
    }

    /// <summary>
    /// Playback stream + decoder for a peer. Also used lazily for relayed guests, who
    /// never "connect" to us — their first packet is the only notice we get.
    /// </summary>
    private void EnsureOutput(int peerId)
    {
        if (_outputs.ContainsKey(peerId)) return;
        try
        {
            _outputs[peerId] = CreateOutput();
#pragma warning disable CS0618
            _decoders[peerId] = new OpusDecoder(SampleRate, Channels);
#pragma warning restore CS0618
        }
        catch (Exception ex) { AppLog.Warn("voice", $"output for peer {peerId} failed: {ex.Message}"); }
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
                MMDevice? commsDev = null;
                if (InputDeviceId is { Length: > 0 } id)
                {
                    try
                    {
                        var chosen = enumerator.GetDevice(id);
                        if (chosen.State == DeviceState.Active) commsDev = chosen;
                    }
                    catch (Exception ex) { AppLog.Warn("voice", $"input device {id} unavailable, using default: {ex.Message}"); }
                }
                commsDev ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                var wasapi = new WasapiCapture(commsDev, false, FrameMs);

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
                if (!_monitorOnly) StatusChanged?.Invoke(Loc.T("Vc_MicOn"));
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
                if (!_monitorOnly) StatusChanged?.Invoke(Loc.T("Vc_MicOn"));
            }
        }
        catch (Exception ex)
        {
            _isMicActive = false;
            MicChanged?.Invoke(false);
            StatusChanged?.Invoke(Loc.T("Vc_MicUnavailable", ex.Message));
            AppLog.Error("voice", "capture init failed", ex);
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
        InputLevel        = 0;
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
        if (encoder == null || buffer == null || converted == null) return;

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
        if (encoder == null) { _captureTailLen = 0; return; }

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

        // Whether this frame may leave the machine at all; the level meter runs either way.
        bool canSend = IsMicActive && _p2p.IsRunning && (!PushToTalk || TalkKeyDown);

        while (_captureTailLen - offset >= FrameBytes)
        {
            Buffer.BlockCopy(_captureTail, offset, pcm, 0, FrameBytes);
            offset += FrameBytes;

            float rms = RmsOf(pcm, FrameSamples);
            InputLevel = LevelFromRms(rms * _micGain);

            if (!canSend) continue;
            if (rms < NoiseGateRms) continue; // noise gate

            ApplyGain(pcm, pcm.Length, _micGain);

            try
            {
#pragma warning disable CS0618
                int len = encoder.Encode(pcm, 0, FrameSamples, encoded, 0, encoded.Length);
#pragma warning restore CS0618
                if (len > 0)
                {
                    _p2p.SendToAll(MessageType.VoiceData, encoded[..len], DeliveryMethod.Unreliable);
                    _lastSent = DateTime.UtcNow;
                }
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

        // Relayed guests arrive under a virtual id with no connect event.
        if (peerId < 0 && !_outputs.ContainsKey(peerId)) EnsureOutput(peerId);

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

    /// <summary>RMS → 0..1 on a dB scale: −60 dBFS reads as 0, 0 dBFS as 1.</summary>
    private static float LevelFromRms(float rms)
    {
        if (rms <= 1f) return 0f;
        double db = 20 * Math.Log10(rms / 32768.0);
        return (float)Math.Clamp((db + 60) / 60, 0, 1);
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
        lock (_captureLock) { if (!_monitorOnly) StopCapture(); }

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

        _monitorOnly = false;
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
