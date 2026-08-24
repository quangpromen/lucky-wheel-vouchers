using System;
using LuckyWheel.Infrastructure.Public;
using Xunit;

namespace LuckyWheel.UnitTests.Application.Public;

public sealed class CryptoRandomNumberProviderTests
{
    private readonly CryptoRandomNumberProvider _provider = new();

    [Fact]
    public void GetInt32_GeneratesValueWithinRange()
    {
        for (int i = 0; i < 50; i++)
        {
            var value = _provider.GetInt32(10, 20);
            Assert.InRange(value, 10, 19);
        }
    }

    [Fact]
    public void GetInt64_GeneratesValueWithinRange()
    {
        for (int i = 0; i < 50; i++)
        {
            var value = _provider.GetInt64(1_000_000_000L, 2_000_000_000L);
            Assert.InRange(value, 1_000_000_000L, 1_999_999_999L);
        }
    }

    [Fact]
    public void GetBytes_ReturnsRequestedLength()
    {
        var bytes = _provider.GetBytes(32);
        Assert.Equal(32, bytes.Length);
    }

    [Fact]
    public void GetInt32_InvalidRange_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _provider.GetInt32(10, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => _provider.GetInt32(10, 10));
    }
}
