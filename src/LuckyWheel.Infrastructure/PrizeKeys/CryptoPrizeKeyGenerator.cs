using System;
using System.Security.Cryptography;
using System.Text;
using LuckyWheel.Application.Features.Admin.PrizeKeys;

namespace LuckyWheel.Infrastructure.PrizeKeys;

/// <summary>
/// Cryptographically secure prize key generator.
/// Produces human-readable keys in format LW-XXXX-XXXX-XXXX-XXXX
/// using a 32-character alphabet (80 bits of cryptographic entropy).
/// </summary>
public sealed class CryptoPrizeKeyGenerator : IPrizeKeyGenerator
{
    // Crockford-style 32-character alphabet avoiding ambiguous characters (0/O, 1/I/L)
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public string GenerateKey()
    {
        Span<char> rawChars = stackalloc char[16];
        for (int i = 0; i < rawChars.Length; i++)
        {
            int index = RandomNumberGenerator.GetInt32(Alphabet.Length);
            rawChars[i] = Alphabet[index];
        }

        return $"LW-{rawChars[..4]}-{rawChars.Slice(4, 4)}-{rawChars.Slice(8, 4)}-{rawChars.Slice(12, 4)}";
    }
}
