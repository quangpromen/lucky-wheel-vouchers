using System;
using LuckyWheel.Domain.Common;

namespace LuckyWheel.Domain.Entities;

public class WheelVersionPrize : AuditableEntity
{
    public Guid WheelVersionId { get; private set; }
    public Guid? PrizeId { get; private set; }
    public int ProbabilityWeight { get; private set; }
    public int DisplayOrder { get; private set; }
    public string Color { get; private set; }
    public string? ImageUrl { get; private set; }
    public bool IsNoPrize { get; private set; }

    public WheelVersionPrize(
        Guid wheelVersionId,
        Guid? prizeId,
        int probabilityWeight,
        int displayOrder,
        string color,
        string? imageUrl,
        bool isNoPrize,
        DateTime createdAtUtc)
    {
        if (wheelVersionId == Guid.Empty)
            throw new DomainException("WVP_INVALID_VERSION_ID", "WheelVersionId cannot be empty.");
        if (probabilityWeight < 0)
            throw new DomainException("WVP_INVALID_WEIGHT", "ProbabilityWeight cannot be negative.");
        if (displayOrder <= 0)
            throw new DomainException("WVP_INVALID_ORDER", "DisplayOrder must be > 0.");
        if (string.IsNullOrWhiteSpace(color))
            throw new DomainException("WVP_COLOR_REQUIRED", "Color is required.");
            
        if (!isNoPrize && prizeId == null)
            throw new DomainException("WVP_PRIZE_ID_REQUIRED", "PrizeId is required if it is not a NoPrize option.");
        if (isNoPrize && prizeId != null)
            throw new DomainException("WVP_PRIZE_ID_MUST_BE_NULL", "PrizeId must be null for NoPrize option.");

        WheelVersionId = wheelVersionId;
        PrizeId = prizeId;
        ProbabilityWeight = probabilityWeight;
        DisplayOrder = displayOrder;
        Color = color.Trim();
        ImageUrl = imageUrl?.Trim();
        IsNoPrize = isNoPrize;
        CreatedAtUtc = createdAtUtc;
    }

    public void UpdateConfiguration(
        int probabilityWeight,
        int displayOrder,
        string color,
        string? imageUrl,
        DateTime updatedAtUtc)
    {
        if (probabilityWeight < 0)
            throw new DomainException("WVP_INVALID_WEIGHT", "ProbabilityWeight cannot be negative.");
        if (displayOrder <= 0)
            throw new DomainException("WVP_INVALID_ORDER", "DisplayOrder must be > 0.");
        if (string.IsNullOrWhiteSpace(color))
            throw new DomainException("WVP_COLOR_REQUIRED", "Color is required.");

        ProbabilityWeight = probabilityWeight;
        DisplayOrder = displayOrder;
        Color = color.Trim();
        ImageUrl = imageUrl?.Trim();
        UpdatedAtUtc = updatedAtUtc;
    }
}
