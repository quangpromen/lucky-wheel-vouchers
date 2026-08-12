using System;
using LuckyWheel.Domain.Common;

namespace LuckyWheel.Domain.Entities;

public class Wheel : AuditableEntity
{
    public string Name { get; private set; }
    public string Slug { get; private set; }
    public string? Description { get; private set; }
    public string Terms { get; private set; }
    public bool IsEnabled { get; private set; }

    public Wheel(
        string name,
        string slug,
        string? description,
        string terms,
        DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("WHEEL_NAME_REQUIRED", "Name is required.");
        if (string.IsNullOrWhiteSpace(slug))
            throw new DomainException("WHEEL_SLUG_REQUIRED", "Slug is required.");
        if (string.IsNullOrWhiteSpace(terms))
            throw new DomainException("WHEEL_TERMS_REQUIRED", "Terms are required.");

        Name = name.Trim();
        if (Name.Length > 200)
            throw new DomainException("WHEEL_NAME_TOO_LONG", "Name cannot exceed 200 characters.");

        Slug = slug.Trim();
        if (Slug.Length > 200)
            throw new DomainException("WHEEL_SLUG_TOO_LONG", "Slug cannot exceed 200 characters.");

        Description = description?.Trim();
        Terms = terms;
        IsEnabled = true;
        CreatedAtUtc = createdAtUtc;
    }

    public void Update(
        string name,
        string slug,
        string? description,
        string terms,
        DateTime updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("WHEEL_NAME_REQUIRED", "Name is required.");
        if (string.IsNullOrWhiteSpace(slug))
            throw new DomainException("WHEEL_SLUG_REQUIRED", "Slug is required.");
        if (string.IsNullOrWhiteSpace(terms))
            throw new DomainException("WHEEL_TERMS_REQUIRED", "Terms are required.");

        Name = name.Trim();
        if (Name.Length > 200)
            throw new DomainException("WHEEL_NAME_TOO_LONG", "Name cannot exceed 200 characters.");

        Slug = slug.Trim();
        if (Slug.Length > 200)
            throw new DomainException("WHEEL_SLUG_TOO_LONG", "Slug cannot exceed 200 characters.");

        Description = description?.Trim();
        Terms = terms;
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
