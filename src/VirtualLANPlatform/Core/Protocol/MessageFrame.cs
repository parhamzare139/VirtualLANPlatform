using System.Text;

namespace VirtualLANPlatform.Core.Protocol;

/// <summary>
/// Wire format: [Magic:2][Version:1][Type:1][PayloadLength:4][Payload:N]
///
/// Header is always plaintext for routing.
/// Payload is plaintext in Phase 1–4.
/// In Phase 5 (Encryption), plug ICryptoProvider into TransportLayer —
/// the frame structure does not change.
/// </summary>
public readonly struct MessageFrame
{
    // "VL" — Virtual LAN
    public static readonly byte[] Magic = [0x56, 0x4C];
    public const byte ProtocolVersion = 0x01;
    public const int HeaderSize = 8; // 2+1+1+4

    public MessageType Type { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public MessageFrame(MessageType type, ReadOnlyMemory<byte> payload)
    {
        Type = type;
        Payload = payload;
    }

    public static MessageFrame Create(MessageType type, byte[] payload)
        => new(type, payload);

    public static MessageFrame FromText(MessageType type, string text)
        => new(type, Encoding.UTF8.GetBytes(text));

    public string PayloadAsText()
        => Encoding.UTF8.GetString(Payload.Span);

    public byte[] Serialize()
    {
        int totalLength = HeaderSize + Payload.Length;
        byte[] buffer = new byte[totalLength];
        int pos = 0;

        buffer[pos++] = Magic[0];
        buffer[pos++] = Magic[1];
        buffer[pos++] = ProtocolVersion;
        buffer[pos++] = (byte)Type;

        uint length = (uint)Payload.Length;
        buffer[pos++] = (byte)(length >> 24);
        buffer[pos++] = (byte)(length >> 16);
        buffer[pos++] = (byte)(length >> 8);
        buffer[pos++] = (byte)length;

        Payload.Span.CopyTo(buffer.AsSpan(pos));
        return buffer;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out MessageFrame frame, out int bytesConsumed)
    {
        frame = default;
        bytesConsumed = 0;

        if (data.Length < HeaderSize) return false;
        if (data[0] != Magic[0] || data[1] != Magic[1]) return false;

        var type = (MessageType)data[3];
        uint length = ((uint)data[4] << 24) | ((uint)data[5] << 16)
                    | ((uint)data[6] << 8)  | data[7];

        // A hostile or corrupt peer could send a length that overflows when cast to
        // int (e.g. 0xFFFFFFFF becomes -1) and crashes the Slice call below instead
        // of just failing this one frame.
        if (length > int.MaxValue - HeaderSize) return false;

        int total = HeaderSize + (int)length;
        if (data.Length < total) return false;

        frame = new MessageFrame(type, data.Slice(HeaderSize, (int)length).ToArray());
        bytesConsumed = total;
        return true;
    }
}
