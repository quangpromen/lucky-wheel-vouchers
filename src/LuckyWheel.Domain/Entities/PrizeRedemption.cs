using System;
using LuckyWheel.Domain.Common;

namespace LuckyWheel.Domain.Entities;

public class PrizeRedemption : Entity
{
    public Guid SpinId { get; private set; }
    public Guid PrizeKeyId { get; private set; }
    public Guid ConfirmedByAdminId { get; private set; }
    public DateTime ConfirmedAtUtc { get; private set; }
    public string? Note { get; private set; }

    public PrizeRedemption(
        Guid spinId,
        Guid prizeKeyId,
        Guid confirmedByAdminId,
        DateTime confirmedAtUtc,
        string? note)
    {
        if (spinId == Guid.Empty)
            throw new DomainException("REDEMPTION_INVALID_SPIN_ID", "SpinId cannot be empty.");
        if (prizeKeyId == Guid.Empty)
            throw new DomainException("REDEMPTION_INVALID_PRIZE_KEY_ID", "PrizeKeyId cannot be empty.");
        if (confirmedByAdminId == Guid.Empty)
            throw new DomainException("REDEMPTION_INVALID_ADMIN_ID", "ConfirmedByAdminId cannot be empty.");

        SpinId = spinId;
        PrizeKeyId = prizeKeyId;
        ConfirmedByAdminId = confirmedByAdminId;
        ConfirmedAtUtc = confirmedAtUtc;
        Note = note;
    }
}
