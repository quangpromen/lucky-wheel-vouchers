using System;
using LuckyWheel.Domain.Common;

namespace LuckyWheel.Domain.Entities;

public class WinnerLock : Entity
{
    public Guid WheelId { get; private set; }
    public string EmailNormalized { get; private set; }
    public Guid SpinId { get; private set; }
    public Guid PrizeKeyId { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsBlocked { get; private set; }
    public DateTime LockedAtUtc { get; private set; }
    public DateTime? UnlockedAtUtc { get; private set; }
    public Guid? UnlockedByAdminId { get; private set; }
    public string? BlockReason { get; private set; }

    public WinnerLock(
        Guid wheelId,
        string emailNormalized,
        Guid spinId,
        Guid prizeKeyId,
        DateTime lockedAtUtc)
    {
        if (wheelId == Guid.Empty)
            throw new DomainException("LOCK_INVALID_WHEEL_ID", "WheelId cannot be empty.");
        if (string.IsNullOrWhiteSpace(emailNormalized))
            throw new DomainException("LOCK_EMAIL_REQUIRED", "EmailNormalized is required.");
        if (spinId == Guid.Empty)
            throw new DomainException("LOCK_INVALID_SPIN_ID", "SpinId cannot be empty.");
        if (prizeKeyId == Guid.Empty)
            throw new DomainException("LOCK_INVALID_PRIZE_KEY_ID", "PrizeKeyId cannot be empty.");

        WheelId = wheelId;
        EmailNormalized = emailNormalized;
        SpinId = spinId;
        PrizeKeyId = prizeKeyId;
        IsActive = true;
        IsBlocked = false;
        LockedAtUtc = lockedAtUtc;
    }

    public void Unlock(
        Guid unlockedByAdminId,
        DateTime unlockedAtUtc)
    {
        if (!IsActive)
            throw new DomainException("LOCK_NOT_ACTIVE", "Lock is not active or already unlocked.");
        if (unlockedByAdminId == Guid.Empty)
            throw new DomainException("LOCK_UNLOCK_ADMIN_REQUIRED", "AdminId is required for unlocking.");

        IsActive = false;
        IsBlocked = false;
        UnlockedByAdminId = unlockedByAdminId;
        UnlockedAtUtc = unlockedAtUtc;
    }

    public void Block(
        string reason,
        DateTime blockedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("LOCK_BLOCK_REASON_REQUIRED", "Reason is required for blocking.");

        IsActive = true;
        IsBlocked = true;
        BlockReason = reason;
        // Keep LockedAtUtc or we could update a timestamp if needed, but per requirements we just set IsBlocked
    }
}
