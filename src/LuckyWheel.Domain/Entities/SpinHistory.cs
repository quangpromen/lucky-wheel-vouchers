using System;
using LuckyWheel.Domain.Common;
using LuckyWheel.Domain.Enums;

namespace LuckyWheel.Domain.Entities;

public class SpinHistory : Entity
{
    public Guid WheelId { get; private set; }
    public Guid WheelVersionId { get; private set; }
    public string EmailOriginal { get; private set; }
    public string EmailNormalized { get; private set; }
    public SpinResult Result { get; private set; }
    public SpinStatus Status { get; private set; }
    public Guid? PrizeId { get; private set; }
    public Guid? PrizeKeyId { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public string ReceiptToken { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public Guid? CancelledByAdminId { get; private set; }
    public string? CancellationReason { get; private set; }

    private SpinHistory(
        Guid wheelId,
        Guid wheelVersionId,
        string emailOriginal,
        string emailNormalized,
        SpinResult result,
        Guid? prizeId,
        Guid? prizeKeyId,
        Guid idempotencyKey,
        string receiptToken,
        DateTime createdAtUtc)
    {
        if (wheelId == Guid.Empty)
            throw new DomainException("SPIN_INVALID_WHEEL_ID", "WheelId cannot be empty.");
        if (wheelVersionId == Guid.Empty)
            throw new DomainException("SPIN_INVALID_VERSION_ID", "WheelVersionId cannot be empty.");
        if (string.IsNullOrWhiteSpace(emailOriginal))
            throw new DomainException("SPIN_EMAIL_REQUIRED", "EmailOriginal is required.");
        if (string.IsNullOrWhiteSpace(emailNormalized))
            throw new DomainException("SPIN_NORMALIZED_EMAIL_REQUIRED", "EmailNormalized is required.");
        if (idempotencyKey == Guid.Empty)
            throw new DomainException("SPIN_INVALID_IDEMPOTENCY_KEY", "IdempotencyKey cannot be empty.");
        if (string.IsNullOrWhiteSpace(receiptToken))
            throw new DomainException("SPIN_RECEIPT_TOKEN_REQUIRED", "ReceiptToken is required.");

        WheelId = wheelId;
        WheelVersionId = wheelVersionId;
        EmailOriginal = emailOriginal;
        EmailNormalized = emailNormalized;
        Result = result;
        Status = SpinStatus.Completed;
        PrizeId = prizeId;
        PrizeKeyId = prizeKeyId;
        IdempotencyKey = idempotencyKey;
        ReceiptToken = receiptToken;
        CreatedAtUtc = createdAtUtc;
    }

    public static SpinHistory CreateNoPrize(
        Guid wheelId,
        Guid wheelVersionId,
        string emailOriginal,
        string emailNormalized,
        Guid idempotencyKey,
        string receiptToken,
        DateTime createdAtUtc)
    {
        return new SpinHistory(
            wheelId,
            wheelVersionId,
            emailOriginal,
            emailNormalized,
            SpinResult.NoPrize,
            null,
            null,
            idempotencyKey,
            receiptToken,
            createdAtUtc);
    }

    public static SpinHistory CreateWin(
        Guid wheelId,
        Guid wheelVersionId,
        string emailOriginal,
        string emailNormalized,
        Guid prizeId,
        Guid prizeKeyId,
        Guid idempotencyKey,
        string receiptToken,
        DateTime createdAtUtc)
    {
        if (prizeId == Guid.Empty)
            throw new DomainException("SPIN_WIN_REQUIRES_PRIZE_ID", "PrizeId is required for Win result.");
        if (prizeKeyId == Guid.Empty)
            throw new DomainException("SPIN_WIN_REQUIRES_PRIZE_KEY_ID", "PrizeKeyId is required for Win result.");

        return new SpinHistory(
            wheelId,
            wheelVersionId,
            emailOriginal,
            emailNormalized,
            SpinResult.Win,
            prizeId,
            prizeKeyId,
            idempotencyKey,
            receiptToken,
            createdAtUtc);
    }

    public void Cancel(
        Guid cancelledByAdminId,
        string reason,
        DateTime cancelledAtUtc)
    {
        if (Status != SpinStatus.Completed)
            throw new DomainException("SPIN_ALREADY_CANCELLED", "Spin is already cancelled.");
        if (Result != SpinResult.Win)
            throw new DomainException("SPIN_CANNOT_CANCEL_NOPRIZE", "Cannot cancel a NoPrize spin.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("SPIN_CANCEL_REASON_REQUIRED", "Reason is required for cancellation.");
        if (cancelledByAdminId == Guid.Empty)
            throw new DomainException("SPIN_CANCEL_ADMIN_REQUIRED", "AdminId is required for cancellation.");

        Status = SpinStatus.Cancelled;
        CancelledByAdminId = cancelledByAdminId;
        CancellationReason = reason;
        CancelledAtUtc = cancelledAtUtc;
    }
}
