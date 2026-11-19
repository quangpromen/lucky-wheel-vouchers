using System;
using LuckyWheel.Domain.Common;
using LuckyWheel.Domain.Enums;

namespace LuckyWheel.Domain.Entities;

public class SpinHistory : Entity
{
    public const string LegacyMissingPrizeNameFallback = "Phần thưởng";

    public Guid WheelId { get; private set; }
    public Guid WheelVersionId { get; private set; }
    public Guid WheelVersionPrizeId { get; private set; }
    public string EmailOriginal { get; private set; }
    public string EmailNormalized { get; private set; }
    public SpinResult Result { get; private set; }
    public SpinStatus Status { get; private set; }
    public Guid? PrizeId { get; private set; }
    public string? PrizeNameSnapshot { get; private set; }
    public string? PrizeDescriptionSnapshot { get; private set; }
    public Guid? PrizeKeyId { get; private set; }
    public string IdempotencyKey { get; private set; }
    public string ReceiptToken { get; private set; }
    public DateTime AcceptedTermsAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public Guid? CancelledByAdminId { get; private set; }
    public string? CancellationReason { get; private set; }

    private SpinHistory(
        Guid wheelId,
        Guid wheelVersionId,
        Guid wheelVersionPrizeId,
        string emailOriginal,
        string emailNormalized,
        SpinResult result,
        Guid? prizeId,
        string? prizeNameSnapshot,
        string? prizeDescriptionSnapshot,
        Guid? prizeKeyId,
        string idempotencyKey,
        string receiptToken,
        DateTime acceptedTermsAtUtc,
        DateTime createdAtUtc)
    {
        if (wheelId == Guid.Empty)
            throw new DomainException("SPIN_INVALID_WHEEL_ID", "WheelId cannot be empty.");
        if (wheelVersionId == Guid.Empty)
            throw new DomainException("SPIN_INVALID_VERSION_ID", "WheelVersionId cannot be empty.");
        if (wheelVersionPrizeId == Guid.Empty)
            throw new DomainException("SPIN_INVALID_VERSION_PRIZE_ID", "WheelVersionPrizeId cannot be empty.");
        if (string.IsNullOrWhiteSpace(emailOriginal))
            throw new DomainException("SPIN_EMAIL_REQUIRED", "EmailOriginal is required.");
        if (string.IsNullOrWhiteSpace(emailNormalized))
            throw new DomainException("SPIN_NORMALIZED_EMAIL_REQUIRED", "EmailNormalized is required.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new DomainException("SPIN_INVALID_IDEMPOTENCY_KEY", "IdempotencyKey cannot be empty.");
        if (string.IsNullOrWhiteSpace(receiptToken))
            throw new DomainException("SPIN_RECEIPT_TOKEN_REQUIRED", "ReceiptToken is required.");

        WheelId = wheelId;
        WheelVersionId = wheelVersionId;
        WheelVersionPrizeId = wheelVersionPrizeId;
        EmailOriginal = emailOriginal;
        EmailNormalized = emailNormalized;
        Result = result;
        Status = SpinStatus.Completed;
        PrizeId = prizeId;
        PrizeNameSnapshot = prizeNameSnapshot;
        PrizeDescriptionSnapshot = prizeDescriptionSnapshot;
        PrizeKeyId = prizeKeyId;
        IdempotencyKey = idempotencyKey;
        ReceiptToken = receiptToken;
        AcceptedTermsAtUtc = acceptedTermsAtUtc;
        CreatedAtUtc = createdAtUtc;
    }

    public static SpinHistory CreateNoPrize(
        Guid wheelId,
        Guid wheelVersionId,
        Guid wheelVersionPrizeId,
        string emailOriginal,
        string emailNormalized,
        string idempotencyKey,
        string receiptToken,
        DateTime acceptedTermsAtUtc,
        DateTime createdAtUtc)
    {
        return new SpinHistory(
            wheelId,
            wheelVersionId,
            wheelVersionPrizeId,
            emailOriginal,
            emailNormalized,
            SpinResult.NoPrize,
            null,
            null,
            null,
            null,
            idempotencyKey,
            receiptToken,
            acceptedTermsAtUtc,
            createdAtUtc);
    }

    public static SpinHistory CreateWin(
        Guid wheelId,
        Guid wheelVersionId,
        Guid wheelVersionPrizeId,
        string emailOriginal,
        string emailNormalized,
        Guid prizeId,
        string prizeNameSnapshot,
        string? prizeDescriptionSnapshot,
        Guid? prizeKeyId,
        string idempotencyKey,
        string receiptToken,
        DateTime acceptedTermsAtUtc,
        DateTime createdAtUtc)
    {
        if (prizeId == Guid.Empty)
            throw new DomainException("SPIN_WIN_REQUIRES_PRIZE_ID", "PrizeId is required for Win result.");
        if (string.IsNullOrWhiteSpace(prizeNameSnapshot))
            throw new DomainException("SPIN_WIN_REQUIRES_PRIZE_NAME_SNAPSHOT", "PrizeNameSnapshot is required for Win result.");

        var trimmedPrizeName = prizeNameSnapshot.Trim();
        if (trimmedPrizeName.Length > 200)
            throw new DomainException("SPIN_WIN_PRIZE_NAME_SNAPSHOT_TOO_LONG", "PrizeNameSnapshot cannot exceed 200 characters.");

        var trimmedPrizeDescription = prizeDescriptionSnapshot?.Trim();
        if (trimmedPrizeDescription is { Length: > 1000 })
            throw new DomainException("SPIN_WIN_PRIZE_DESCRIPTION_SNAPSHOT_TOO_LONG", "PrizeDescriptionSnapshot cannot exceed 1000 characters.");

        if (prizeKeyId.HasValue && prizeKeyId.Value == Guid.Empty)
            throw new DomainException("SPIN_WIN_REQUIRES_PRIZE_KEY_ID", "PrizeKeyId cannot be empty when provided.");

        return new SpinHistory(
            wheelId,
            wheelVersionId,
            wheelVersionPrizeId,
            emailOriginal,
            emailNormalized,
            SpinResult.Win,
            prizeId,
            trimmedPrizeName,
            trimmedPrizeDescription,
            prizeKeyId,
            idempotencyKey,
            receiptToken,
            acceptedTermsAtUtc,
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
