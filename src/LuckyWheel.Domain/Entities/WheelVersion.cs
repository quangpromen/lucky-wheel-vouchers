using System;
using LuckyWheel.Domain.Common;
using LuckyWheel.Domain.Enums;

namespace LuckyWheel.Domain.Entities;

public class WheelVersion : AuditableEntity
{
    public Guid WheelId { get; private set; }
    public int VersionNumber { get; private set; }
    public WheelVersionStatus Status { get; private set; }
    public DateTime StartAtUtc { get; private set; }
    public DateTime EndAtUtc { get; private set; }
    public int ClaimDurationMinutes { get; private set; }
    public DateTime? PublishedAtUtc { get; private set; }
    public Guid? PublishedByAdminId { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }

    public WheelVersion(
        Guid wheelId,
        int versionNumber,
        DateTime startAtUtc,
        DateTime endAtUtc,
        int claimDurationMinutes,
        DateTime createdAtUtc)
    {
        if (wheelId == Guid.Empty)
            throw new DomainException("WHEEL_VERSION_INVALID_WHEEL_ID", "WheelId cannot be empty.");
        if (versionNumber <= 0)
            throw new DomainException("WHEEL_VERSION_INVALID_NUMBER", "VersionNumber must be > 0.");
        if (endAtUtc <= startAtUtc)
            throw new DomainException("WHEEL_VERSION_INVALID_PERIOD", "EndAtUtc must be greater than StartAtUtc.");
        if (claimDurationMinutes <= 0)
            throw new DomainException("WHEEL_VERSION_INVALID_CLAIM_DURATION", "ClaimDurationMinutes must be > 0.");

        WheelId = wheelId;
        VersionNumber = versionNumber;
        Status = WheelVersionStatus.Draft;
        StartAtUtc = startAtUtc;
        EndAtUtc = endAtUtc;
        ClaimDurationMinutes = claimDurationMinutes;
        CreatedAtUtc = createdAtUtc;
    }

    public void UpdateSchedule(
        DateTime startAtUtc,
        DateTime endAtUtc,
        int claimDurationMinutes,
        DateTime updatedAtUtc)
    {
        if (Status != WheelVersionStatus.Draft)
            throw new DomainException("WHEEL_VERSION_CANNOT_BE_EDITED", "Only Draft version can be edited.");
        if (endAtUtc <= startAtUtc)
            throw new DomainException("WHEEL_VERSION_INVALID_PERIOD", "EndAtUtc must be greater than StartAtUtc.");
        if (claimDurationMinutes <= 0)
            throw new DomainException("WHEEL_VERSION_INVALID_CLAIM_DURATION", "ClaimDurationMinutes must be > 0.");

        StartAtUtc = startAtUtc;
        EndAtUtc = endAtUtc;
        ClaimDurationMinutes = claimDurationMinutes;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void Activate(
        Guid publishedByAdminId,
        DateTime publishedAtUtc)
    {
        if (Status != WheelVersionStatus.Draft)
            throw new DomainException("WHEEL_VERSION_CANNOT_BE_ACTIVATED", "Only Draft version can be activated.");
        if (publishedByAdminId == Guid.Empty)
            throw new DomainException("WHEEL_VERSION_INVALID_PUBLISHER", "PublishedByAdminId cannot be empty.");

        Status = WheelVersionStatus.Active;
        PublishedByAdminId = publishedByAdminId;
        PublishedAtUtc = publishedAtUtc;
        UpdatedAtUtc = publishedAtUtc;
    }

    public void Close(DateTime closedAtUtc)
    {
        if (Status != WheelVersionStatus.Active)
            throw new DomainException("WHEEL_VERSION_CANNOT_BE_CLOSED", "Only Active version can be closed.");

        Status = WheelVersionStatus.Closed;
        ClosedAtUtc = closedAtUtc;
        UpdatedAtUtc = closedAtUtc;
    }
}
