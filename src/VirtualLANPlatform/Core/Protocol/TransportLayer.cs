namespace VirtualLANPlatform.Core.Protocol;

/// <summary>
/// Encryption-ready transport layer.
///
/// Phase 1–4: pack/unpack is a thin wrapper around MessageFrame.
/// Phase 5:   set CryptoProvider — payload is encrypted/decrypted
///            transparently without changing the frame structure.
/// </summary>
public class TransportLayer
{
    // Placeholder for Phase 5:
    // public ICryptoProvider? CryptoProvider { get; set; }

    public byte[] Pack(MessageType type, byte[] payload)
    {
        // Phase 5: payload = CryptoProvider?.Encrypt(payload) ?? payload;
        return new MessageFrame(type, payload).Serialize();
    }

    public byte[] PackText(MessageType type, string text)
        => Pack(type, System.Text.Encoding.UTF8.GetBytes(text));

    public bool TryUnpack(byte[] data, out MessageFrame frame)
    {
        if (!MessageFrame.TryDeserialize(data, out frame, out _))
            return false;

        // Phase 5: decrypt frame.Payload here via CryptoProvider
        return true;
    }
}
