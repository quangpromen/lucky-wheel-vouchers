using System;

namespace LuckyWheel.Infrastructure.PrizeKeys;

public sealed class PrizeKeyProtectionOptions
{
    public const string SectionName = "PrizeKeyProtection";

    /// <summary>
    /// Base64 encoded 256-bit (32 bytes) AES-GCM encryption key.
    /// Loaded securely from User Secrets or Environment Variables.
    /// </summary>
    public string EncryptionKey { get; set; } = string.Empty;

    /// <summary>
    /// Validates that EncryptionKey is configured, is a valid Base64 string,
    /// and decodes to exactly 32 bytes (256-bit key for AES-256-GCM).
    /// Throws InvalidOperationException with safe messages if invalid.
    /// </summary>
    public byte[] GetKeyBytesOrThrow()
    {
        if (string.IsNullOrWhiteSpace(EncryptionKey))
            throw new InvalidOperationException("PrizeKeyProtection encryption key is required but was not provided. Configure 'PrizeKeyProtection:EncryptionKey' via User Secrets or Environment Variables.");

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(EncryptionKey.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("PrizeKeyProtection encryption key is not a valid Base64 encoded string.");
        }

        if (keyBytes.Length != 32)
            throw new InvalidOperationException($"PrizeKeyProtection encryption key must be exactly 32 bytes (256 bits) when Base64 decoded. Actual length: {keyBytes.Length} bytes.");

        return keyBytes;
    }
}
