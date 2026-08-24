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

        public int GetInt32(int fromInclusive, int toExclusive) => (int)_nextValue;
        public long GetInt64(long fromInclusive, long toExclusive) => _nextValue;
        public byte[] GetBytes(int count) => new byte[count];
    }

    [Fact]
    public void Select_SingleCandidate_ReturnsCandidate()
    {
        var versionId = Guid.NewGuid();
        var candidate = new WheelVersionPrize(versionId, null, 1000, 1, "#FFF", null, true, DateTime.UtcNow);
        var selector = new WeightedPrizeSelector(new MockRandomProvider(500));

        var selected = selector.Select([candidate]);

        Assert.Same(candidate, selected);
    }

    [Fact]
    public void Select_BoundaryLowestValue_ReturnsFirstCandidate()
    {
        var versionId = Guid.NewGuid();
        var seg1 = new WheelVersionPrize(versionId, null, 100, 1, "#FFF", null, true, DateTime.UtcNow);
        var seg2 = new WheelVersionPrize(versionId, Guid.NewGuid(), 200, 2, "#FFF", null, false, DateTime.UtcNow);
        var seg3 = new WheelVersionPrize(versionId, Guid.NewGuid(), 700, 3, "#FFF", null, false, DateTime.UtcNow);

        // Random value = 0 (lowest boundary)
        var selector = new WeightedPrizeSelector(new MockRandomProvider(0));
        var selected = selector.Select([seg1, seg2, seg3]);

        Assert.Same(seg1, selected);
    }

    [Fact]
    public void Select_MiddleValue_ReturnsExpectedCandidate()
    {
        var versionId = Guid.NewGuid();
        var seg1 = new WheelVersionPrize(versionId, null, 100, 1, "#FFF", null, true, DateTime.UtcNow);
        var seg2 = new WheelVersionPrize(versionId, Guid.NewGuid(), 200, 2, "#FFF", null, false, DateTime.UtcNow);
        var seg3 = new WheelVersionPrize(versionId, Guid.NewGuid(), 700, 3, "#FFF", null, false, DateTime.UtcNow);

        // seg1: [0..100), seg2: [100..300), seg3: [300..1000)
        // Value = 150 falls into seg2
        var selector = new WeightedPrizeSelector(new MockRandomProvider(150));
        var selected = selector.Select([seg1, seg2, seg3]);

        Assert.Same(seg2, selected);
    }

    [Fact]
    public void Select_BoundaryHighestValue_ReturnsLastCandidate()
    {
        var versionId = Guid.NewGuid();
        var seg1 = new WheelVersionPrize(versionId, null, 100, 1, "#FFF", null, true, DateTime.UtcNow);
        var seg2 = new WheelVersionPrize(versionId, Guid.NewGuid(), 200, 2, "#FFF", null, false, DateTime.UtcNow);
        var seg3 = new WheelVersionPrize(versionId, Guid.NewGuid(), 700, 3, "#FFF", null, false, DateTime.UtcNow);

        // Total weight = 1000. Random value = 999 (last possible index in [0, 1000))
        var selector = new WeightedPrizeSelector(new MockRandomProvider(999));
        var selected = selector.Select([seg1, seg2, seg3]);

        Assert.Same(seg3, selected);
    }

    [Fact]
    public void Select_EmptyCandidates_ThrowsInvalidOperationException()
    {
        var selector = new WeightedPrizeSelector(new MockRandomProvider(0));

        Assert.Throws<InvalidOperationException>(() => selector.Select([]));
    }
}
