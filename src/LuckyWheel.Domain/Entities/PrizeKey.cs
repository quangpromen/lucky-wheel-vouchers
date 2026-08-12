using System;
using LuckyWheel.Domain.Common;
using LuckyWheel.Domain.Enums;

namespace LuckyWheel.Domain.Entities;

public class PrizeKey : AuditableEntity
{
    public Guid PrizeId { get; private set; }
    public string CodeHash { get; private set; }
    public string CodeEncrypted { get; private set; }
    public PrizeKeyStatus Status { get; private set; }
    public Guid? AssignedSpinId { get; private set; }
    public DateTime? AssignedAtUtc { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public DateTime? RedeemedAtUtc { get; private set; }
    public DateTime? ExpiredAtUtc { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }

    public PrizeKey(
        Guid prizeId,
        string codeHash,
        string codeEncrypted,
        DateTime createdAtUtc)
    {
        if (prizeId == Guid.Empty)
            throw new DomainException("PRIZE_KEY_INVALID_PRIZE_ID", "PrizeId cannot be empty.");
        if (string.IsNullOrWhiteSpace(codeHash))
            throw new DomainException("PRIZE_KEY_HASH_REQUIRED", "CodeHash is required.");
        if (string.IsNullOrWhiteSpace(codeEncrypted))
            throw new DomainException("PRIZE_KEY_ENCRYPTED_REQUIRED", "CodeEncrypted is required.");

        PrizeId = prizeId;
        CodeHash = codeHash;
        CodeEncrypted = codeEncrypted;
        Status = PrizeKeyStatus.Available;
        CreatedAtUtc = createdAtUtc;
    }

    public void Assign(
        Guid spinId,
        DateTime assignedAtUtc,
        DateTime expiresAtUtc)
    {
        if (Status != PrizeKeyStatus.Available)
            throw new DomainException("PRIZE_KEY_INVALID_STATUS", "Only Available key can be assigned.");
        if (spinId == Guid.Empty)
            throw new DomainException("PRIZE_KEY_INVALID_SPIN_ID", "SpinId cannot be empty.");
        if (expiresAtUtc <= assignedAtUtc)
            throw new DomainException("PRIZE_KEY_INVALID_ASSIGNMENT_PERIOD", "ExpiresAtUtc must be greater than AssignedAtUtc.");

        Status = PrizeKeyStatus.Assigned;
        AssignedSpinId = spinId;
        AssignedAtUtc = assignedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        UpdatedAtUtc = assignedAtUtc;
    }

    public void Redeem(DateTime redeemedAtUtc)
    {
        if (Status != PrizeKeyStatus.Assigned)
            throw new DomainException("PRIZE_KEY_CANNOT_BE_REDEEMED", "Only Assigned key can be redeemed.");
        if (redeemedAtUtc < AssignedAtUtc)
            throw new DomainException("PRIZE_KEY_INVALID_REDEEM_TIME", "Cannot redeem before assigned time.");
        if (redeemedAtUtc >= ExpiresAtUtc)
            throw new DomainException("PRIZE_KEY_ALREADY_EXPIRED", "Key is already expired and cannot be redeemed.");

        Status = PrizeKeyStatus.Redeemed;
        RedeemedAtUtc = redeemedAtUtc;
        UpdatedAtUtc = redeemedAtUtc;
    }

    public void Expire(DateTime expiredAtUtc)
    {
        if (Status != PrizeKeyStatus.Assigned)
            throw new DomainException("PRIZE_KEY_CANNOT_BE_EXPIRED", "Only Assigned key can be expired.");
        if (expiredAtUtc < ExpiresAtUtc)
            throw new DomainException("PRIZE_KEY_INVALID_EXPIRE_TIME", "Cannot expire key before its expiration time.");

        Status = PrizeKeyStatus.Expired;
        ExpiredAtUtc = expiredAtUtc;
        UpdatedAtUtc = expiredAtUtc;
    }

    public void Cancel(DateTime cancelledAtUtc)
    {
        if (Status != PrizeKeyStatus.Assigned)
            throw new DomainException("PRIZE_KEY_CANNOT_BE_CANCELLED", "Only Assigned key can be cancelled.");
        if (cancelledAtUtc < AssignedAtUtc)
            throw new DomainException("PRIZE_KEY_INVALID_CANCEL_TIME", "Cannot cancel before assigned time.");

        Status = PrizeKeyStatus.Cancelled;
        CancelledAtUtc = cancelledAtUtc;
        UpdatedAtUtc = cancelledAtUtc;
    }
}
