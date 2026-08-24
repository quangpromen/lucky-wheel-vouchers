using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LuckyWheel.Application.Features.Public;

namespace LuckyWheel.Infrastructure.Public;

public sealed class CryptoRandomNumberProvider : IRandomNumberProvider
{
    public int GetInt32(int fromInclusive, int toExclusive)
    {
        if (fromInclusive >= toExclusive)
            throw new ArgumentOutOfRangeException(nameof(fromInclusive), "fromInclusive must be less than toExclusive.");

        return RandomNumberGenerator.GetInt32(fromInclusive, toExclusive);
    }

    public long GetInt64(long fromInclusive, long toExclusive)
    {
        if (fromInclusive >= toExclusive)
            throw new ArgumentOutOfRangeException(nameof(fromInclusive), "fromInclusive must be less than toExclusive.");

        ulong range = (ulong)(toExclusive - fromInclusive);
        ulong limit = ulong.MaxValue - (ulong.MaxValue % range);
        ulong value;
        Span<byte> bytes = stackalloc byte[8];

        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = MemoryMarshal.Read<ulong>(bytes);
        } while (value >= limit);

        return fromInclusive + (long)(value % range);
    }

    public byte[] GetBytes(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive.");

        return RandomNumberGenerator.GetBytes(count);
    }
}
