using System;
using System.Security.Cryptography;
using System.Text;
using LuckyWheel.Application.Features.Admin.PrizeKeys;
using Microsoft.Extensions.Options;

namespace LuckyWheel.Infrastructure.PrizeKeys;

/// <summary>
/// Implements IPrizeKeyProtector using SHA-256 for CodeHash
/// and AES-256-GCM (AEAD) for CodeEncrypted payload.
/// </summary>
public sealed class AesGcmPrizeKeyProtector(IOptions<PrizeKeyProtectionOptions> options) : IPrizeKeyProtector
{
    private const int NonceSize = 12; // 96-bit nonce for AES-GCM
    private const int TagSize = 16;   // 128-bit authentication tag

    public string Normalize(string plaintextKey)
    {
        if (string.IsNullOrWhiteSpace(plaintextKey))
            throw new ArgumentException("Plaintext key cannot be null or empty.", nameof(plaintextKey));

        return plaintextKey.Trim().ToUpperInvariant();
    }

    public string ComputeHash(string plaintextKey)
    {
        var normalized = Normalize(plaintextKey);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes);
    }

    public ProtectedPrizeKey Protect(string plaintextKey)
    {
        var normalized = Normalize(plaintextKey);
        var codeHash = ComputeHash(normalized);
        var keyBytes = options.Value.GetKeyBytesOrThrow();

        var plaintextBytes = Encoding.UTF8.GetBytes(normalized);
        var ciphertext = new byte[plaintextBytes.Length];
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];

        RandomNumberGenerator.Fill(nonce);

        using (var aesGcm = new AesGcm(keyBytes, TagSize))
        {
            aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }

        return new ProtectedPrizeKey(
            codeHash,
            ciphertext,
            nonce,
            tag);
    }

    public string Unprotect(byte[] encryptedCode, byte[] encryptionNonce, byte[] encryptionTag)
    {
        if (encryptedCode is not { Length: > 0 } || encryptionNonce is not { Length: NonceSize } || encryptionTag is not { Length: TagSize })
            throw new ArgumentException("Encrypted code, nonce, and tag are required.");

        var keyBytes = options.Value.GetKeyBytesOrThrow();

        var decryptedBytes = new byte[encryptedCode.Length];

        using (var aesGcm = new AesGcm(keyBytes, TagSize))
        {
            aesGcm.Decrypt(encryptionNonce, encryptedCode, encryptionTag, decryptedBytes);
        }

        return Encoding.UTF8.GetString(decryptedBytes);
    }
}
