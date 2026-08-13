using System;
using LuckyWheel.Domain.Common;

namespace LuckyWheel.Domain.Entities;

public class AdminUser : AuditableEntity
{
    public string Email { get; private set; }
    public string PasswordHash { get; private set; } = string.Empty;
    public string DisplayName { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime? LastLoginAtUtc { get; private set; }

    public AdminUser(
        string email,
        string displayName,
        DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new DomainException("ADMIN_EMAIL_REQUIRED", "Email is required.");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new DomainException("ADMIN_DISPLAY_NAME_REQUIRED", "DisplayName is required.");

        Email = email.Trim().ToLowerInvariant();
        DisplayName = displayName;
        IsActive = true;
        CreatedAtUtc = createdAtUtc;
    }

    public void SetPasswordHash(string passwordHash, DateTime updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new DomainException("ADMIN_PASSWORD_HASH_REQUIRED", "Password hash is required.");

        PasswordHash = passwordHash;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void RecordLogin(DateTime loggedInAtUtc)
    {
        LastLoginAtUtc = loggedInAtUtc;
        UpdatedAtUtc = loggedInAtUtc;
    }

    public void Activate(DateTime updatedAtUtc)
    {
        IsActive = true;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void Deactivate(DateTime updatedAtUtc)
    {
        IsActive = false;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void UpdateDisplayName(
        string displayName,
        DateTime updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new DomainException("ADMIN_DISPLAY_NAME_REQUIRED", "DisplayName is required.");

        DisplayName = displayName;
        UpdatedAtUtc = updatedAtUtc;
    }
}
