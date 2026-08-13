using System;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Infrastructure.Time;
using Xunit;

namespace LuckyWheel.UnitTests.Application.Time;

/// <summary>
/// Unit tests for <see cref="SystemClock"/>.
/// </summary>
public sealed class SystemClockTests
{
    [Fact]
    public void UtcNow_Returns_UtcOffset()
    {
        IClock clock = new SystemClock();

        var now = clock.UtcNow;

        Assert.Equal(TimeSpan.Zero, now.Offset);
    }

    [Fact]
    public void UtcNow_Is_CloseToSystemUtcNow()
    {
        IClock clock = new SystemClock();

        var before = DateTimeOffset.UtcNow;
        var clockNow = clock.UtcNow;
        var after = DateTimeOffset.UtcNow;

        Assert.True(clockNow >= before, "Clock time should be at or after test start.");
        Assert.True(clockNow <= after, "Clock time should be at or before test end.");
    }

    [Fact]
    public void UtcNow_CalledTwice_SecondCallIsNotEarlierThanFirst()
    {
        IClock clock = new SystemClock();

        var first = clock.UtcNow;
        var second = clock.UtcNow;

        Assert.True(second >= first, "Time should not go backward.");
    }

    [Fact]
    public void SystemClock_Implements_IClock()
    {
        var clock = new SystemClock();

        Assert.IsAssignableFrom<IClock>(clock);
    }
}
