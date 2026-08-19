using System;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LuckyWheel.Infrastructure.PrizeKeys;
using Microsoft.Extensions.Options;
using Xunit;

namespace LuckyWheel.UnitTests.Application.PrizeKeys;

public sealed class PrizeKeyCryptoTests
{
    private static readonly Regex KeyPattern = new(@"^LW-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4}$", RegexOptions.Compiled);

    [Fact]
    public void CryptoPrizeKeyGenerator_GeneratesValidFormat()
    {
        var generator = new CryptoPrizeKeyGenerator();

        for (int i = 0; i < 50; i++)
        {
            var key = generator.GenerateKey();
            Assert.NotNull(key);
            Assert.Equal(22, key.Length);
            Assert.Matches(KeyPattern, key);
        }
    }

    [Fact]
    public void CryptoPrizeKeyGenerator_ConsecutiveKeysAreDistinct()
    {
        var generator = new CryptoPrizeKeyGenerator();
        var keys = new HashSet<string>();

        for (int i = 0; i < 100; i++)
        {
            var key = generator.GenerateKey();
            Assert.DoesNotContain(key, keys);
            keys.Add(key);
        }
    }

    [Theory]
    [InlineData("  lw-2345-6789-abcd-efgh  ", "LW-2345-6789-ABCD-EFGH")]
    [InlineData("LW-AAAA-BBBB-CCCC-DDDD", "LW-AAAA-BBBB-CCCC-DDDD")]
    [InlineData("lw-xyz2-3456-789a-bcde", "LW-XYZ2-3456-789A-BCDE")]
    public void AesGcmPrizeKeyProtector_Normalize_IsConsistent(string input, string expected)
    {
        var options = CreateValidOptions();
        var protector = new AesGcmPrizeKeyProtector(options);

        var normalized = protector.Normalize(input);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void AesGcmPrizeKeyProtector_Normalize_NullOrWhitespace_ThrowsArgumentException()
    {
        var options = CreateValidOptions();
        var protector = new AesGcmPrizeKeyProtector(options);

        Assert.Throws<ArgumentException>(() => protector.Normalize(""));
        Assert.Throws<ArgumentException>(() => protector.Normalize("   "));
        Assert.Throws<ArgumentException>(() => protector.Normalize(null!));
    }

    [Fact]
    public void AesGcmPrizeKeyProtector_ComputeHash_SamePlaintextYieldsSameHash()
    {
        var options = CreateValidOptions();
        var protector = new AesGcmPrizeKeyProtector(options);

        var hash1 = protector.ComputeHash("  lw-1234-5678-90ab-cdef  ");
        var hash2 = protector.ComputeHash("LW-1234-5678-90AB-CDEF");

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
    }

    [Fact]
    public void AesGcmPrizeKeyProtector_ComputeHash_DifferentPlaintextsYieldDifferentHashes()
    {
        var options = CreateValidOptions();
        var protector = new AesGcmPrizeKeyProtector(options);

        var hash1 = protector.ComputeHash("LW-AAAA-AAAA-AAAA-AAAA");
        var hash2 = protector.ComputeHash("LW-BBBB-BBBB-BBBB-BBBB");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void AesGcmPrizeKeyProtector_ProtectAndUnprotect_RoundtripSucceeds()
    {
        var options = CreateValidOptions();
        var protector = new AesGcmPrizeKeyProtector(options);

        var originalKey = "LW-2345-6789-ABCD-EFGH";
        var protectedKey = protector.Protect(originalKey);

        Assert.NotNull(protectedKey.CodeHash);
        Assert.Equal(64, protectedKey.CodeHash.Length);
        Assert.NotNull(protectedKey.EncryptedCode);
        Assert.NotEmpty(protectedKey.EncryptedCode);

        var decrypted = protector.Unprotect(protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag);
        Assert.Equal(originalKey, decrypted);
    }

    [Fact]
    public void AesGcmPrizeKeyProtector_SamePlaintextTwice_UsesDifferentNonceAndCiphertext()
    {
        var protector = new AesGcmPrizeKeyProtector(CreateValidOptions());

        var first = protector.Protect("LW-2345-6789-ABCD-EFGH");
        var second = protector.Protect("LW-2345-6789-ABCD-EFGH");

        Assert.False(first.EncryptionNonce.SequenceEqual(second.EncryptionNonce));
        Assert.False(first.EncryptedCode.SequenceEqual(second.EncryptedCode));
    }

    [Fact]
    public void AesGcmPrizeKeyProtector_TamperedCiphertext_ThrowsCryptographicException()
    {
        var options = CreateValidOptions();
        var protector = new AesGcmPrizeKeyProtector(options);

        var originalKey = "LW-2345-6789-ABCD-EFGH";
        var protectedKey = protector.Protect(originalKey);
        var rawBytes = protectedKey.EncryptedCode.ToArray();
        rawBytes[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(rawBytes, protectedKey.EncryptionNonce, protectedKey.EncryptionTag));
    }

    [Fact]
    public void AesGcmPrizeKeyProtector_TamperedTag_ThrowsCryptographicException()
    {
        var options = CreateValidOptions();
        var protector = new AesGcmPrizeKeyProtector(options);

        var originalKey = "LW-2345-6789-ABCD-EFGH";
        var protectedKey = protector.Protect(originalKey);
        var rawBytes = protectedKey.EncryptionTag.ToArray();
        rawBytes[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(protectedKey.EncryptedCode, protectedKey.EncryptionNonce, rawBytes));
    }

    [Fact]
    public void AesGcmPrizeKeyProtector_TamperedNonce_ThrowsCryptographicException()
    {
        var protector = new AesGcmPrizeKeyProtector(CreateValidOptions());
        var protectedKey = protector.Protect("LW-2345-6789-ABCD-EFGH");
        var rawBytes = protectedKey.EncryptionNonce.ToArray();
        rawBytes[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(
            () => protector.Unprotect(protectedKey.EncryptedCode, rawBytes, protectedKey.EncryptionTag));
    }

    [Fact]
    public void PrizeKeyProtectionOptions_InvalidBase64_ThrowsInvalidOperationException()
    {
        var options = new PrizeKeyProtectionOptions { EncryptionKey = "not-valid-base64!!!" };
        Assert.Throws<InvalidOperationException>(() => options.GetKeyBytesOrThrow());
    }

    [Fact]
    public void PrizeKeyProtectionOptions_WrongKeyLength_ThrowsInvalidOperationException()
    {
        // 16 bytes instead of 32 bytes
        var options = new PrizeKeyProtectionOptions { EncryptionKey = Convert.ToBase64String(new byte[16]) };
        var ex = Assert.Throws<InvalidOperationException>(() => options.GetKeyBytesOrThrow());
        Assert.Contains("32 bytes", ex.Message);
    }

    [Fact]
    public void PrizeKeyProtectionOptions_MissingKey_ThrowsInvalidOperationException()
    {
        var options = new PrizeKeyProtectionOptions { EncryptionKey = "" };
        Assert.Throws<InvalidOperationException>(() => options.GetKeyBytesOrThrow());
    }

    private static IOptions<PrizeKeyProtectionOptions> CreateValidOptions()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return Options.Create(new PrizeKeyProtectionOptions { EncryptionKey = key });
    }
}
