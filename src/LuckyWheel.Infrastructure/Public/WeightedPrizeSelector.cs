using System;
using System.Collections.Generic;
using LuckyWheel.Application.Features.Public;
using LuckyWheel.Domain.Entities;

namespace LuckyWheel.Infrastructure.Public;

public sealed class WeightedPrizeSelector(IRandomNumberProvider randomProvider) : IWeightedPrizeSelector
{
    private readonly IRandomNumberProvider _randomProvider = randomProvider ?? throw new ArgumentNullException(nameof(randomProvider));

    public WheelVersionPrize Select(IReadOnlyList<WheelVersionPrize> eligibleCandidates)
    {
        if (eligibleCandidates == null || eligibleCandidates.Count == 0)
            throw new InvalidOperationException("Cannot select prize from an empty candidate list.");

        long totalWeight = 0;
        for (int i = 0; i < eligibleCandidates.Count; i++)
        {
            var candidate = eligibleCandidates[i];
            if (candidate.ProbabilityWeight <= 0)
                throw new InvalidOperationException($"Candidate {candidate.Id} has invalid weight {candidate.ProbabilityWeight}.");

            totalWeight += candidate.ProbabilityWeight;
        }

        if (totalWeight <= 0)
            throw new InvalidOperationException("Total probability weight must be greater than zero.");

        long randomValue = _randomProvider.GetInt64(0, totalWeight);
        long cumulativeWeight = 0;

        for (int i = 0; i < eligibleCandidates.Count; i++)
        {
            var candidate = eligibleCandidates[i];
            cumulativeWeight += candidate.ProbabilityWeight;
            if (randomValue < cumulativeWeight)
            {
                return candidate;
            }
        }

        return eligibleCandidates[^1];
    }
}
