using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteNetLib;
using VirtualLANPlatform.Core.Networking;
using VirtualLANPlatform.Core.Protocol;

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

    private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingAccept = [];
    private readonly Dictionary<string, IncomingState>              _incoming      = [];

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
        _p2p.MessageReceived += OnP2PMessage;
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    // externalId lets the caller pre-generate the id so the UI can track the card immediately
    public async Task SendFileAsync(string filePath, string? externalId = null)
    {
        if (!_p2p.IsRunning || _p2p.PeerCount == 0)
        {
            StatusChanged?.Invoke("هیچ Peer متصلی وجود ندارد");
            return;
        }

        var sysInfo = new System.IO.FileInfo(filePath);
        if (!sysInfo.Exists) return;

        string id   = externalId ?? Guid.NewGuid().ToString("N")[..12];
        string name = sysInfo.Name;
        long   size = sysInfo.Length;

        if (size == 0)
        {
            StatusChanged?.Invoke($"فایل «{name}» خالی است و قابل ارسال نیست");
            TransferFailed?.Invoke(id, "فایل خالی است (0 بایت)");
            return;
        }

        int totalChunks = (int)Math.Ceiling((double)size / ChunkSize);

        StatusChanged?.Invoke($"در حال محاسبه SHA256: {name}");
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
        StatusChanged?.Invoke($"در انتظار پذیرش: {name}");

        bool accepted;
        try   { accepted = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { accepted = false; }
        finally { _pendingAccept.Remove(id); }

        if (!accepted)
        {
            TransferFailed?.Invoke(id, "طرف مقابل فایل را نپذیرفت");
            StatusChanged?.Invoke("ارسال فایل لغو شد");
            return;
        }

        // Stream chunks
        StatusChanged?.Invoke($"در حال ارسال: {name}  (0 / {totalChunks})");
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
                TransferProgress?.Invoke(id, i + 1, totalChunks);

                if (i % 50 == 0)
                    StatusChanged?.Invoke($"ارسال: {name}  ({i + 1} / {totalChunks})");

                if (i % 100 == 0) await Task.Yield();
            }
        });

        StatusChanged?.Invoke($"ارسال کامل شد: {name}");
        TransferComplete?.Invoke(id, filePath);
    }

    // ── Accept / Reject ───────────────────────────────────────────────────────

    public void AcceptTransfer(string id, string savePath)
    {
        if (!_incoming.TryGetValue(id, out var state))
        {
            StatusChanged?.Invoke($"خطا: اطلاعات فایل یافت نشد (id={id})");
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
            _incoming.Remove(id);
            TransferFailed?.Invoke(id, ex.Message);
        }
    }

    public void RejectTransfer(string id)
    {
        if (!_incoming.TryGetValue(id, out var state)) return;
        _incoming.Remove(id);
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

            _incoming[id] = new IncomingState(id, name, size, chunks, sha256, peerId,
                null, null, null, 0);
            IncomingFile?.Invoke(new FileTransferInfo(id, name, size, peerId));
        }
        catch { }
    }

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
                    $"دریافت: {state.Name}  ({received} / {state.TotalChunks})");

            if (received >= state.TotalChunks)
                FinalizeTransfer(id, state);
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
            StatusChanged?.Invoke($"در حال بررسی SHA256: {state.Name}");
            using (var fs = File.OpenRead(tempPath))
            {
                string computed = Convert.ToHexString(SHA256.HashData(fs)).ToLower();
                if (computed != state.ExpectedSha256)
                {
                    try { File.Delete(tempPath); } catch { }
                    _incoming.Remove(id);
                    TransferFailed?.Invoke(id, $"خطای Checksum — فایل آسیب دیده: {state.Name}");
                    return;
                }
            }

            // Move verified file to Downloads
            if (File.Exists(state.SavePath!)) File.Delete(state.SavePath!);
            File.Move(tempPath, state.SavePath!);

            _incoming.Remove(id);
            TransferComplete?.Invoke(id, state.SavePath!);
        }
        catch (Exception ex)
        {
            _incoming.Remove(id);
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
