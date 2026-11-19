using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace LuckyWheel.Infrastructure.PrizeKeys;

public sealed class PrizeKeyExpirationOptions : IValidatableObject
{
    public const string SectionName = "PrizeKeyExpiration";

    public int IntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 100;
    public bool Enabled { get; set; } = true;
    public int DefaultClaimDurationMinutes { get; set; } = 60;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IntervalSeconds < 1)
            yield return new ValidationResult("IntervalSeconds must be at least 1 second.", [nameof(IntervalSeconds)]);

        if (BatchSize is < 1 or > 1000)
            yield return new ValidationResult("BatchSize must be between 1 and 1000.", [nameof(BatchSize)]);

        if (DefaultClaimDurationMinutes < 1)
            yield return new ValidationResult("DefaultClaimDurationMinutes must be at least 1 minute.", [nameof(DefaultClaimDurationMinutes)]);
    }
}
