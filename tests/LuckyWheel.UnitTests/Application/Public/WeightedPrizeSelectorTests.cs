using System;
using System.Collections.Generic;
using LuckyWheel.Application.Features.Public;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Infrastructure.Public;
using Xunit;

namespace LuckyWheel.UnitTests.Application.Public;

public sealed class WeightedPrizeSelectorTests
{
    private sealed class MockRandomProvider(long nextValue) : IRandomNumberProvider
    {
        private readonly long _nextValue = nextValue;

        public long? LastFromInclusive { get; private set; }
        public long? LastToExclusive { get; private set; }

        public int GetInt32(int fromInclusive, int toExclusive) => (int)_nextValue;
        public long GetInt64(long fromInclusive, long toExclusive)
        {
            LastFromInclusive = fromInclusive;
            LastToExclusive = toExclusive;
            return _nextValue;
        }
        public byte[] GetBytes(int count) => new byte[count];
    }

    [Fact]
    public void Select_SingleCandidate_ReturnsCandidate()
    {
        var versionId = Guid.NewGuid();
        var candidate = new WheelVersionPrize(versionId, null, 1_000_000, 1, "#FFF", null, true, DateTime.UtcNow);
        var random = new MockRandomProvider(500_000);
        var selector = new WeightedPrizeSelector(random);

        var selected = selector.Select([candidate]);

        Assert.Same(candidate, selected);
        Assert.Equal(0L, random.LastFromInclusive);
        Assert.Equal(1_000_000L, random.LastToExclusive);
    }

    [Fact]
    public void Select_BoundaryLowestValue_ReturnsFirstCandidate()
    {
        var versionId = Guid.NewGuid();
        var seg1 = new WheelVersionPrize(versionId, null, 100_000, 1, "#FFF", null, true, DateTime.UtcNow);
        var seg2 = new WheelVersionPrize(versionId, Guid.NewGuid(), 200_000, 2, "#FFF", null, false, DateTime.UtcNow);
        var seg3 = new WheelVersionPrize(versionId, Guid.NewGuid(), 700_000, 3, "#FFF", null, false, DateTime.UtcNow);

        // Random value = 0 (lowest boundary)
        var selector = new WeightedPrizeSelector(new MockRandomProvider(0));
        var selected = selector.Select([seg1, seg2, seg3]);

        Assert.Same(seg1, selected);
    }

    [Fact]
    public void Select_MiddleValue_ReturnsExpectedCandidate()
    {
        var versionId = Guid.NewGuid();
        var seg1 = new WheelVersionPrize(versionId, null, 100_000, 1, "#FFF", null, true, DateTime.UtcNow);
        var seg2 = new WheelVersionPrize(versionId, Guid.NewGuid(), 200_000, 2, "#FFF", null, false, DateTime.UtcNow);
        var seg3 = new WheelVersionPrize(versionId, Guid.NewGuid(), 700_000, 3, "#FFF", null, false, DateTime.UtcNow);

        // seg1: [0..100_000), seg2: [100_000..300_000), seg3: [300_000..1_000_000)
        // Value = 150_000 falls into seg2
        var selector = new WeightedPrizeSelector(new MockRandomProvider(150_000));
        var selected = selector.Select([seg1, seg2, seg3]);

        Assert.Same(seg2, selected);
    }

    [Fact]
    public void Select_BoundaryHighestValue_ReturnsLastCandidate()
    {
        var versionId = Guid.NewGuid();
        var seg1 = new WheelVersionPrize(versionId, null, 100_000, 1, "#FFF", null, true, DateTime.UtcNow);
        var seg2 = new WheelVersionPrize(versionId, Guid.NewGuid(), 200_000, 2, "#FFF", null, false, DateTime.UtcNow);
        var seg3 = new WheelVersionPrize(versionId, Guid.NewGuid(), 700_000, 3, "#FFF", null, false, DateTime.UtcNow);

        // Total weight = 1_000_000. Random value = 999_999 (last possible index in [0, 1_000_000))
        var selector = new WeightedPrizeSelector(new MockRandomProvider(999_999));
        var selected = selector.Select([seg1, seg2, seg3]);

        Assert.Same(seg3, selected);
    }

    [Theory]
    [InlineData(0L, 1)]
    [InlineData(499_999L, 1)]
    [InlineData(500_000L, 2)]
    [InlineData(799_999L, 2)]
    [InlineData(800_000L, 3)]
    [InlineData(999_999L, 3)]
    public void Select_OneMillionScaleBoundaries_MapsExactIntervals(long randomValue, int expectedDisplayOrder)
    {
        var versionId = Guid.NewGuid();
        var segA = new WheelVersionPrize(versionId, Guid.NewGuid(), 500_000, 1, "#FF0000", null, false, DateTime.UtcNow);
        var segB = new WheelVersionPrize(versionId, Guid.NewGuid(), 300_000, 2, "#00FF00", null, false, DateTime.UtcNow);
        var noPrize = new WheelVersionPrize(versionId, null, 200_000, 3, "#CCCCCC", null, true, DateTime.UtcNow);

        var random = new MockRandomProvider(randomValue);
        var selector = new WeightedPrizeSelector(random);

        var selected = selector.Select([segA, segB, noPrize]);

        Assert.Equal(expectedDisplayOrder, selected.DisplayOrder);
        Assert.Equal(0L, random.LastFromInclusive);
        Assert.Equal(1_000_000L, random.LastToExclusive);
    }

    [Theory]
    [InlineData(300_000, 200_000)]
    [InlineData(499_999, 500_000)]
    [InlineData(500_001, 500_000)]
    public void Select_TotalWeightNotEqualToOneMillion_ThrowsInvalidOperationException(int weight1, int weight2)
    {
        var versionId = Guid.NewGuid();
        var seg1 = new WheelVersionPrize(versionId, Guid.NewGuid(), weight1, 1, "#FF0000", null, false, DateTime.UtcNow);
        var seg2 = new WheelVersionPrize(versionId, null, weight2, 2, "#CCCCCC", null, true, DateTime.UtcNow);

        var selector = new WeightedPrizeSelector(new MockRandomProvider(0));

        Assert.Throws<InvalidOperationException>(() => selector.Select([seg1, seg2]));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1_000_000L)]
    public void Select_RandomValueOutOfRange_ThrowsInvalidOperationException(long invalidRandomValue)
    {
        var versionId = Guid.NewGuid();
        var seg = new WheelVersionPrize(versionId, null, 1_000_000, 1, "#CCCCCC", null, true, DateTime.UtcNow);

        var selector = new WeightedPrizeSelector(new MockRandomProvider(invalidRandomValue));

        Assert.Throws<InvalidOperationException>(() => selector.Select([seg]));
    }

    [Fact]
    public void Select_EmptyCandidates_ThrowsInvalidOperationException()
    {
        var selector = new WeightedPrizeSelector(new MockRandomProvider(0));

        Assert.Throws<InvalidOperationException>(() => selector.Select([]));
    }
}
