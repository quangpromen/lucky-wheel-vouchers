using System;
using LuckyWheel.Domain.Common;

namespace LuckyWheel.Domain.Entities;

public class Prize : AuditableEntity
{
    public Guid WheelId { get; private set; }
    public string Name { get; private set; }
    public string? Description { get; private set; }
    public string? ImageUrl { get; private set; }
    public bool RequiresKey { get; private set; }
    public int TotalQuantity { get; private set; }
    public bool IsEnabled { get; private set; }

    public Prize(
        Guid wheelId,
        string name,
        string? description,
        string? imageUrl,
        bool requiresKey,
        int totalQuantity,
        DateTime createdAtUtc)
    {
        if (wheelId == Guid.Empty)
            throw new DomainException("PRIZE_INVALID_WHEEL_ID", "WheelId cannot be empty.");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("PRIZE_NAME_REQUIRED", "Name is required.");
        if (totalQuantity < 0)
            throw new DomainException("PRIZE_INVALID_QUANTITY", "TotalQuantity cannot be negative.");
        if (requiresKey && totalQuantity <= 0)
            throw new DomainException("PRIZE_KEY_REQUIRES_QUANTITY", "TotalQuantity must be > 0 if RequiresKey is true.");
            
        Name = name.Trim();
        if (Name.Length > 200)
            throw new DomainException("PRIZE_NAME_TOO_LONG", "Name cannot exceed 200 characters.");

        WheelId = wheelId;
        Description = description?.Trim();
        ImageUrl = imageUrl?.Trim();
        RequiresKey = requiresKey;
        TotalQuantity = totalQuantity;
        IsEnabled = true;
        CreatedAtUtc = createdAtUtc;
    }

    public void Update(
        string name,
        string? description,
        string? imageUrl,
        bool requiresKey,
        int totalQuantity,
        DateTime updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("PRIZE_NAME_REQUIRED", "Name is required.");
        if (totalQuantity < 0)
            throw new DomainException("PRIZE_INVALID_QUANTITY", "TotalQuantity cannot be negative.");
        if (requiresKey && totalQuantity <= 0)
            throw new DomainException("PRIZE_KEY_REQUIRES_QUANTITY", "TotalQuantity must be > 0 if RequiresKey is true.");

        Name = name.Trim();
        if (Name.Length > 200)
            throw new DomainException("PRIZE_NAME_TOO_LONG", "Name cannot exceed 200 characters.");

        Description = description?.Trim();
        ImageUrl = imageUrl?.Trim();
        RequiresKey = requiresKey;
        TotalQuantity = totalQuantity;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void Enable(DateTime updatedAtUtc)
    {
        IsEnabled = true;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void Disable(DateTime updatedAtUtc)
    {
        IsEnabled = false;
        UpdatedAtUtc = updatedAtUtc;
    }
}
