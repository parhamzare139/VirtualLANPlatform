using System.Net;
using System.Net.Sockets;

namespace VirtualLANPlatform.Core.Networking;

/// <summary>
/// Encodes and decodes Connection Codes.
///
/// Format (raw bytes before obfuscation):
///   [Version:1][IP:4 or 16][PortHi:1][PortLo:1][CRC8:1]
///
/// The raw bytes are XOR-obfuscated with a static app key, then
/// encoded as Base58 (no ambiguous characters: 0, O, I, l).
///
/// This is NOT a security mechanism — the actual security comes from
/// the encrypted P2P session (Phase 5). The obfuscation simply makes
/// codes opaque to casual inspection.
/// </summary>
public static class ConnectionCodeEngine
{
    private const byte VersionIPv4 = 0x01;
    private const byte VersionIPv6 = 0x02;

    // App-specific obfuscation key — do not change after release
    private static readonly byte[] ObfuscationKey =
    [
        0xA3, 0x7F, 0x2C, 0x8E, 0x51, 0xD4, 0x6B, 0x19,
        0xF8, 0x3A, 0xC7, 0x5E, 0x92, 0x1B, 0x4D, 0xE6,
        0x73, 0x28, 0x9F, 0xB5
    ];

    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static string Encode(IPAddress ip, ushort port)
    {
        bool isV6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        byte version = isV6 ? VersionIPv6 : VersionIPv4;
        byte[] ipBytes = ip.GetAddressBytes();

        // Raw: version + ip + port_hi + port_lo
        byte[] raw = new byte[1 + ipBytes.Length + 2];
        raw[0] = version;
        ipBytes.CopyTo(raw, 1);
        raw[^2] = (byte)(port >> 8);
        raw[^1] = (byte)(port & 0xFF);

        byte[] obfuscated = XorWithKey(raw);
        byte crc = Crc8(obfuscated);

        return Base58Encode([.. obfuscated, crc]);
    }

    public static (IPAddress Ip, ushort Port) Decode(string code)
    {
        byte[] withCrc;
        try { withCrc = Base58Decode(code.Trim()); }
        catch { throw new FormatException("Connection Code نامعتبر است."); }

        if (withCrc.Length < 2)
            throw new FormatException("Connection Code نامعتبر است.");

        byte[] data = withCrc[..^1];
        if (Crc8(data) != withCrc[^1])
            throw new FormatException("Connection Code خراب است — احتمالاً هنگام تایپ اشتباه رخ داده.");

        byte[] raw = XorWithKey(data);
        byte version = raw[0];

        if (version == VersionIPv4)
        {
            // version(1) + ip(4) + port(2) = 7 bytes
            if (raw.Length != 7)
                throw new FormatException("Connection Code نامعتبر است (طول غلط).");
            return (new IPAddress(raw[1..5]), ReadPort(raw, 5));
        }

        if (version == VersionIPv6)
        {
            // version(1) + ip(16) + port(2) = 19 bytes
            if (raw.Length != 19)
                throw new FormatException("Connection Code نامعتبر است (طول غلط).");
            return (new IPAddress(raw[1..17]), ReadPort(raw, 17));
        }

        throw new FormatException("نسخه Connection Code پشتیبانی نمی‌شود.");
    }

    private static ushort ReadPort(byte[] raw, int offset)
        => (ushort)((raw[offset] << 8) | raw[offset + 1]);

    private static byte[] XorWithKey(byte[] data)
    {
        byte[] result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ ObfuscationKey[i % ObfuscationKey.Length]);
        return result;
    }

    private static byte Crc8(byte[] data)
    {
        byte crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
        }
        return crc;
    }

    private static string Base58Encode(byte[] data)
    {
        int zeros = 0;
        while (zeros < data.Length && data[zeros] == 0) zeros++;

        var digits = new List<int>();
        foreach (byte b in data)
        {
            int carry = b;
            for (int j = 0; j < digits.Count; j++)
            {
                carry += 256 * digits[j];
                digits[j] = carry % 58;
                carry /= 58;
            }
            while (carry > 0) { digits.Add(carry % 58); carry /= 58; }
        }

        var result = new char[zeros + digits.Count];
        for (int i = 0; i < zeros; i++) result[i] = Alphabet[0];
        for (int i = 0; i < digits.Count; i++)
            result[zeros + i] = Alphabet[digits[^(i + 1)]];

        return new string(result);
    }

    private static byte[] Base58Decode(string encoded)
    {
        int zeros = 0;
        while (zeros < encoded.Length && encoded[zeros] == '1') zeros++;

        var bytes = new List<byte>();
        foreach (char c in encoded)
        {
            int digit = Alphabet.IndexOf(c);
            if (digit < 0) throw new FormatException($"کاراکتر نامعتبر: '{c}'");

            int carry = digit;
            for (int j = 0; j < bytes.Count; j++)
            {
                carry += 58 * bytes[j];
                bytes[j] = (byte)(carry & 0xFF);
                carry >>= 8;
            }
            while (carry > 0) { bytes.Add((byte)(carry & 0xFF)); carry >>= 8; }
        }

        for (int i = 0; i < zeros; i++) bytes.Add(0);
        bytes.Reverse();
        return [.. bytes];
    }
}
