using System.Security.Cryptography;

namespace VirtualLANPlatform.Core.Security;

/// <summary>
/// Per-session ephemeral ECDH P-256 keypair + AES-256-GCM encryption.
///
/// Flow:
///   1. Each side calls ExportPublicKey() and sends it as MessageType.KeyExchange.
///   2. On receiving the peer's public key, call DeriveSessionKey().
///   3. After that, Encrypt/Decrypt all payloads.
///
/// Wire format for encrypted payload: [Nonce:12][Tag:16][Ciphertext:N]
/// </summary>
public sealed class CryptoEngine : IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize   = 16;

    private readonly ECDiffieHellman _ecdh;
    private bool _disposed;

    public CryptoEngine()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    // ── Key material ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the DER-encoded SubjectPublicKeyInfo (~91 bytes for P-256).
    /// Send this to the peer as the KeyExchange payload.
    /// </summary>
    public byte[] ExportPublicKey() => _ecdh.ExportSubjectPublicKeyInfo();

    /// <summary>
    /// Computes a 32-byte AES session key from the peer's public key (DER format).
    /// </summary>
    public byte[] DeriveSessionKey(byte[] peerPublicKeyDer)
    {
        using var peerEcdh = ECDiffieHellman.Create();
        peerEcdh.ImportSubjectPublicKeyInfo(peerPublicKeyDer, out _);
        // DeriveKeyFromHash applies ECDH then SHA-256 — deterministic 32-byte key
        return _ecdh.DeriveKeyFromHash(peerEcdh.PublicKey, HashAlgorithmName.SHA256);
    }

    // ── Encrypt / Decrypt (static — usable without an instance) ──────────────

    public static byte[] Encrypt(byte[] sessionKey, ReadOnlySpan<byte> plaintext)
    {
        byte[] nonce      = new byte[NonceSize];
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag        = new byte[TagSize];

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(sessionKey, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Layout: [nonce:12][tag:16][ciphertext:N]
        byte[] result = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        ciphertext.CopyTo(result, NonceSize + TagSize);
        return result;
    }

    public static byte[] Decrypt(byte[] sessionKey, byte[] encryptedData)
    {
        if (encryptedData.Length < NonceSize + TagSize)
            throw new CryptographicException(UI.Localization.Loc.T("Crypto_TooShort"));

        var nonce      = encryptedData.AsSpan(0, NonceSize);
        var tag        = encryptedData.AsSpan(NonceSize, TagSize);
        var ciphertext = encryptedData.AsSpan(NonceSize + TagSize);
        byte[] plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(sessionKey, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ecdh.Dispose();
    }
}
