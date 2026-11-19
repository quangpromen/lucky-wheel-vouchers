using System;
using System.Collections.Generic;
using LuckyWheel.Application.Features.Public;
using LuckyWheel.Domain.Entities;

namespace LuckyWheel.Infrastructure.Public;

public sealed class WeightedPrizeSelector(IRandomNumberProvider randomProvider) : IWeightedPrizeSelector
{
    public const long ExpectedTotalWeight = 1_000_000;

    private readonly IRandomNumberProvider _randomProvider = randomProvider ?? throw new ArgumentNullException(nameof(randomProvider));

    public WheelVersionPrize Select(IReadOnlyList<WheelVersionPrize> segments)
    {
        if (segments == null || segments.Count == 0)
            throw new InvalidOperationException("Cannot select prize from an empty segment list.");

        long totalWeight = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i]
                ?? throw new InvalidOperationException("Segment cannot be null.");

            if (segment.ProbabilityWeight <= 0)
                throw new InvalidOperationException($"Segment {segment.Id} has invalid weight {segment.ProbabilityWeight}.");

            totalWeight += segment.ProbabilityWeight;
        }

        if (totalWeight != ExpectedTotalWeight)
            throw new InvalidOperationException($"Total probability weight must equal {ExpectedTotalWeight}, but was {totalWeight}.");

        long randomValue = _randomProvider.GetInt64(0, ExpectedTotalWeight);
        if (randomValue < 0 || randomValue >= ExpectedTotalWeight)
            throw new InvalidOperationException($"Random value {randomValue} is out of valid range [0, {ExpectedTotalWeight}).");

        long cumulativeWeight = 0;

        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            cumulativeWeight += segment.ProbabilityWeight;
            if (randomValue < cumulativeWeight)
            {
                return segment;
            }
        }

        return segments[^1];
    }
}
