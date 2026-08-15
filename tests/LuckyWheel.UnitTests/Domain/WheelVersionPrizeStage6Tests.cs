using LuckyWheel.Domain.Common;
using LuckyWheel.Domain.Entities;

namespace LuckyWheel.UnitTests.Domain;

public sealed class WheelVersionPrizeStage6Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveWeight(int weight)
    {
        var ex = Assert.Throws<DomainException>(() => new WheelVersionPrize(
            Guid.NewGuid(), null, weight, 1, "#ffffff", null, true, DateTime.UtcNow));
        Assert.Equal("WVP_INVALID_WEIGHT", ex.Code);
    }

    [Fact]
    public void Constructor_RejectsPrizeOnNoPrizeSegment() => Assert.Throws<DomainException>(() =>
        new WheelVersionPrize(Guid.NewGuid(), Guid.NewGuid(), 1, 1, "#ffffff", null, true, DateTime.UtcNow));

    [Fact]
    public void Constructor_RequiresPrizeOnPrizeSegment() => Assert.Throws<DomainException>(() =>
        new WheelVersionPrize(Guid.NewGuid(), null, 1, 1, "#ffffff", null, false, DateTime.UtcNow));
}
