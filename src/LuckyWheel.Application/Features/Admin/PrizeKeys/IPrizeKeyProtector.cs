namespace LuckyWheel.Application.Features.Admin.PrizeKeys;

/// <summary>
/// Abstraction for normalizing, hashing (SHA-256), encrypting (AES-256-GCM),
/// and decrypting prize keys. Plaintext keys are never stored directly.
/// </summary>
public interface IPrizeKeyProtector
{
    /// <summary>Normalizes plaintext key (trim and uppercase).</summary>
    string Normalize(string plaintextKey);

    /// <summary>Computes deterministic SHA-256 hash of normalized key for unique indexing.</summary>
    string ComputeHash(string plaintextKey);

    /// <summary>Normalizes, hashes, and encrypts the plaintext key using AES-256-GCM.</summary>
    ProtectedPrizeKey Protect(string plaintextKey);

    /// <summary>Decrypts the protected key payload back to normalized plaintext key.</summary>
    string Unprotect(byte[] encryptedCode, byte[] encryptionNonce, byte[] encryptionTag);
}

public sealed record ProtectedPrizeKey(
    string CodeHash,
    byte[] EncryptedCode,
    byte[] EncryptionNonce,
    byte[] EncryptionTag);
