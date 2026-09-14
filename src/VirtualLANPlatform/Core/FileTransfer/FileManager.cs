using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteNetLib;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;

using VirtualLANPlatform.UI.Localization;

namespace VirtualLANPlatform.Core.FileTransfer;

public record FileTransferInfo(string Id, string FileName, long Size, int PeerId);

/// <summary>
/// Phase 8 — P2P file transfer.
/// Send: FileInfo → wait for FileAccept → stream FileChunk messages
/// Receive: show prompt → accept/reject → reassemble → SHA256 verify → save
/// Chunk format: [12 bytes id][4 bytes index][data]
/// </summary>
public sealed class FileManager : IDisposable
{
    private readonly P2PManager _p2p;

    // Touched from both the P2P network thread (OnP2PMessage) and the UI thread
    // (AcceptTransfer/RejectTransfer are called from click handlers) — must be thread-safe.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingAccept = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IncomingState>              _incoming      = new();

    private const int ChunkSize = 32768; // 32 KB

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<FileTransferInfo>?   IncomingFile;       // receiver: show accept card
    public event Action<string>?             TransferAccepted;   // sender: peer accepted — id
    public event Action<string, int, int>?   TransferProgress;   // id, done, total chunks
    public event Action<string, string>?     TransferComplete;   // id, final path
    public event Action<string, string>?     TransferFailed;     // id, reason
    public event Action<string>?             StatusChanged;

    // ── Constructor ───────────────────────────────────────────────────────────

    public FileManager(P2PManager p2p)
    {
        _p2p = p2p;
        _p2p.MessageReceived  += OnP2PMessage;
        _p2p.PeerDisconnected += OnPeerDisconnected;
    }

    /// <summary>Fails and cleans up any transfer tied to a peer that just dropped —
    /// otherwise the receiving side is left with an open temp FileStream and a UI
    /// card stuck at "receiving..." forever.</summary>
    private void OnPeerDisconnected(int peerId, string reason)
    {
        foreach (var (id, state) in _incoming)
        {
            if (state.PeerId != peerId) continue;
            if (_incoming.TryRemove(id, out var removed))
            {
                try { removed.TempFile?.Dispose(); } catch { }
                if (removed.TempPath != null) try { File.Delete(removed.TempPath); } catch { }
                TransferFailed?.Invoke(id, Loc.T("Fm_PeerGone"));
            }
        }
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    // externalId lets the caller pre-generate the id so the UI can track the card immediately
    public async Task SendFileAsync(string filePath, string? externalId = null)
    {
        if (!_p2p.IsRunning || _p2p.PeerCount == 0)
        {
            StatusChanged?.Invoke(Loc.T("Fm_NoPeers"));
            return;
        }

        var sysInfo = new System.IO.FileInfo(filePath);
        if (!sysInfo.Exists) return;

        string id   = externalId ?? Guid.NewGuid().ToString("N")[..12];
        string name = sysInfo.Name;
        long   size = sysInfo.Length;

        if (size == 0)
        {
            StatusChanged?.Invoke(Loc.T("Fm_EmptyNamed", name));
            TransferFailed?.Invoke(id, Loc.T("Fm_Empty"));
            return;
        }

        int totalChunks = (int)Math.Ceiling((double)size / ChunkSize);

        // The only caller (TestWindow.xaml.cs) fires this fire-and-forget via
        // `_ = Task.Run(...)` — any unhandled exception here becomes an unobserved
        // task exception, TransferFailed never fires, and the UI card is stuck at
        // "در انتظار پذیرش..." forever with nothing telling the user it failed.
        try
        {
            StatusChanged?.Invoke(Loc.T("Fm_Hashing", name));
            string sha256 = await Task.Run(() =>
            {
                using var fs = File.OpenRead(filePath);
                return Convert.ToHexString(SHA256.HashData(fs)).ToLower();
            });

            // Broadcast FileInfo
            string infoJson = JsonSerializer.Serialize(new
            {
                id, name, size, sha256, chunks = totalChunks
            });

            // Register TCS BEFORE sending FileInfo — prevents race condition on loopback
            // where FileAccept arrives before TCS is registered, leaving it unset forever.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAccept[id] = tcs;

            _p2p.SendToAll(MessageType.FileInfo, Encoding.UTF8.GetBytes(infoJson));
            StatusChanged?.Invoke(Loc.T("Fm_AwaitAccept", name));

            bool accepted;
            try   { accepted = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
            catch { accepted = false; }
            finally { _pendingAccept.TryRemove(id, out _); }

            if (!accepted)
            {
                TransferFailed?.Invoke(id, Loc.T("Fm_Refused"));
                StatusChanged?.Invoke(Loc.T("Fm_SendCancelled"));
                return;
            }

            // Stream chunks
            StatusChanged?.Invoke(Loc.T("Fm_SendStart", name, totalChunks));
            int sentChunks = 0;
            await Task.Run(async () =>
            {
                using var fs  = File.OpenRead(filePath);
                byte[] buf    = new byte[ChunkSize];
                for (int i = 0; i < totalChunks; i++)
                {
                    int read = await fs.ReadAsync(buf.AsMemory(0, ChunkSize));
                    if (read == 0) break;

                    byte[] packet = BuildChunkPacket(id, i, buf, read);
                    _p2p.SendToAll(MessageType.FileChunk, packet, DeliveryMethod.ReliableOrdered);
                    sentChunks = i + 1;
                    TransferProgress?.Invoke(id, sentChunks, totalChunks);

                    if (i % 50 == 0)
                        StatusChanged?.Invoke(Loc.T("Fm_SendChunk", name, i + 1, totalChunks));

                    if (i % 100 == 0) await Task.Yield();
                }
            });

            // The file shrank/became unreadable mid-send: the receiver is still waiting
            // for the original chunk count and will hang forever if we claim success.
            if (sentChunks < totalChunks)
            {
                TransferFailed?.Invoke(id, Loc.T("Fm_Truncated"));
                StatusChanged?.Invoke(Loc.T("Fm_SendFailed", name));
                return;
            }

            StatusChanged?.Invoke(Loc.T("Fm_SendDone", name));
            TransferComplete?.Invoke(id, filePath);
        }
        catch (Exception ex)
        {
            _pendingAccept.TryRemove(id, out _);
            TransferFailed?.Invoke(id, ex.Message);
            StatusChanged?.Invoke(Loc.T("Fm_SendFailed", name));
        }
    }

    // ── Accept / Reject ───────────────────────────────────────────────────────

    public void AcceptTransfer(string id, string savePath)
    {
        if (!_incoming.TryGetValue(id, out var state))
        {
            StatusChanged?.Invoke(Loc.T("Fm_NoMeta", id));
            return;
        }

        try
        {
            // Keep temp file in system temp dir so Downloads stays clean until transfer is done
            string tempPath   = Path.Combine(Path.GetTempPath(), $"vlp_{id}.tmp");
            var    tempStream = File.Create(tempPath);
            _incoming[id]     = state with { SavePath = savePath, TempPath = tempPath,
                                             TempFile = tempStream };

            string resp = JsonSerializer.Serialize(new { id, ok = true });
            _p2p.SendToPeer(state.PeerId, MessageType.FileAccept,
                Encoding.UTF8.GetBytes(resp));
        }
        catch (Exception ex)
        {
            _incoming.TryRemove(id, out _);
            TransferFailed?.Invoke(id, ex.Message);
        }
    }

    public void RejectTransfer(string id)
    {
        if (!_incoming.TryRemove(id, out var state)) return;
        string resp = JsonSerializer.Serialize(new { id, ok = false });
        _p2p.SendToPeer(state.PeerId, MessageType.FileAccept,
            Encoding.UTF8.GetBytes(resp));
    }

    // ── P2P receive ───────────────────────────────────────────────────────────

    private void OnP2PMessage(int peerId, MessageFrame frame)
    {
        switch (frame.Type)
        {
            case MessageType.FileInfo:   HandleFileInfo(peerId, frame.Payload.ToArray());  break;
            case MessageType.FileAccept: HandleFileAccept(frame.Payload.ToArray());        break;
            case MessageType.FileChunk:  HandleFileChunk(frame.Payload.ToArray());         break;
        }
    }

    private void HandleFileInfo(int peerId, byte[] data)
    {
        try
        {
            var root = JsonDocument.Parse(data).RootElement;
            if (!root.TryGetProperty("name", out _)) return; // it's not a file-info request

            string id     = root.GetProperty("id").GetString()!;
            string name   = root.GetProperty("name").GetString()!;
            long   size   = root.GetProperty("size").GetInt64();
            int    chunks = root.GetProperty("chunks").GetInt32();
            string sha256 = root.GetProperty("sha256").GetString()!;

            // id and name come from another peer's machine — never trust them as path
            // components. A rooted or ".."-bearing name would otherwise let a peer make
            // us write/overwrite a file anywhere Path.Combine lets it escape to.
            if (!IsSafeId(id)) return;
            name = Path.GetFileName(name);
            if (name.Length == 0) return;

            // A reused id for an in-progress transfer would otherwise leak the old
            // temp FileStream/file, orphaned once this entry is overwritten.
            if (_incoming.TryRemove(id, out var stale))
            {
                try { stale.TempFile?.Dispose(); } catch { }
                if (stale.TempPath != null) try { File.Delete(stale.TempPath); } catch { }
            }

            _incoming[id] = new IncomingState(id, name, size, chunks, sha256, peerId,
                null, null, null, 0);
            IncomingFile?.Invoke(new FileTransferInfo(id, name, size, peerId));
        }
        catch { }
    }

    // The wire format for FileChunk fixes the id at exactly 12 ASCII bytes
    // (BuildChunkPacket / HandleFileChunk) — an id longer than that would get
    // silently truncated on the chunk side, never matching the key stored here,
    // and the transfer would hang forever with chunks dropped one by one.
    private static bool IsSafeId(string id)
        => id.Length is > 0 and <= 12 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    private void HandleFileAccept(byte[] data)
    {
        try
        {
            var root = JsonDocument.Parse(data).RootElement;
            string id = root.GetProperty("id").GetString()!;
            bool   ok = root.GetProperty("ok").GetBoolean();
            if (_pendingAccept.TryGetValue(id, out var tcs))
                tcs.TrySetResult(ok);
            if (ok)
                TransferAccepted?.Invoke(id);
        }
        catch { }
    }

    private void HandleFileChunk(byte[] data)
    {
        if (data.Length < 16) return;
        try
        {
            string id    = Encoding.ASCII.GetString(data, 0, 12).Trim('\0');
            int    index = BitConverter.ToInt32(data, 12);

            if (!_incoming.TryGetValue(id, out var state) || state.TempFile == null) return;

            state.TempFile.Write(data, 16, data.Length - 16);

            int received = state.ReceivedChunks + 1;
            _incoming[id] = state with { ReceivedChunks = received };
            TransferProgress?.Invoke(id, received, state.TotalChunks);

            if (received % 50 == 0)
                StatusChanged?.Invoke(
                    Loc.T("Fm_RecvChunk", state.Name, received, state.TotalChunks));

            if (received >= state.TotalChunks)
                // SHA256-hashing and moving the whole file is too slow to run inline —
                // this handler runs on the P2P poll thread, which also drives voice
                // playback; blocking it here would stall audio for the whole session.
                _ = Task.Run(() => FinalizeTransfer(id, state));
        }
        catch { }
    }

    private void FinalizeTransfer(string id, IncomingState state)
    {
        try
        {
            state.TempFile?.Flush();
            state.TempFile?.Dispose();

            string tempPath = state.TempPath!;

            // Verify integrity
            StatusChanged?.Invoke(Loc.T("Fm_Verifying", state.Name));
            using (var fs = File.OpenRead(tempPath))
            {
                string computed = Convert.ToHexString(SHA256.HashData(fs)).ToLower();
                if (computed != state.ExpectedSha256)
                {
                    try { File.Delete(tempPath); } catch { }
                    _incoming.TryRemove(id, out _);
                    TransferFailed?.Invoke(id, Loc.T("Fm_Checksum", state.Name));
                    return;
                }
            }

            // Move verified file to Downloads
            if (File.Exists(state.SavePath!)) File.Delete(state.SavePath!);
            File.Move(tempPath, state.SavePath!);

            _incoming.TryRemove(id, out _);
            TransferComplete?.Invoke(id, state.SavePath!);
        }
        catch (Exception ex)
        {
            _incoming.TryRemove(id, out _);
            if (state.TempPath != null) try { File.Delete(state.TempPath); } catch { }
            TransferFailed?.Invoke(id, ex.Message);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] BuildChunkPacket(string id, int index, byte[] data, int len)
    {
        byte[] packet = new byte[16 + len];
        Encoding.ASCII.GetBytes(id.PadRight(12)[..12]).CopyTo(packet, 0);
        BitConverter.GetBytes(index).CopyTo(packet, 12);
        data.AsSpan(0, len).CopyTo(packet.AsSpan(16));
        return packet;
    }

    private static string UniqueFileName(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext  = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
    }

    public string GetSavePath(string fileName)
    {
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        return UniqueFileName(downloads, fileName);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _p2p.MessageReceived -= OnP2PMessage;
        foreach (var (_, s) in _incoming)
        {
            try { s.TempFile?.Dispose(); } catch { }
            if (s.TempPath != null) try { File.Delete(s.TempPath); } catch { }
        }
        _incoming.Clear();
    }
}

// ── Internal ──────────────────────────────────────────────────────────────────

internal sealed record IncomingState(
    string      Id,
    string      Name,
    long        Size,
    int         TotalChunks,
    string      ExpectedSha256,
    int         PeerId,
    string?     SavePath,
    string?     TempPath,
    FileStream? TempFile,
    int         ReceivedChunks
);
