using System;
using LuckyWheel.Domain.Common;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using Xunit;

namespace LuckyWheel.UnitTests.Domain;

public sealed class PrizeKeyLifecycleUnitTests
{
    private static readonly byte[] DummyEncrypted = [1, 2, 3, 4];
    private static readonly byte[] DummyNonce = new byte[12];
    private static readonly byte[] DummyTag = new byte[16];

    [Fact]
    public void Constructor_WithValidData_InitializesAvailableKey()
    {
        var prizeId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;

        var key = new PrizeKey(prizeId, "HASH123", DummyEncrypted, DummyNonce, DummyTag, createdAt);

        Assert.Equal(prizeId, key.PrizeId);
        Assert.Equal("HASH123", key.CodeHash);
        Assert.Equal(PrizeKeyStatus.Available, key.Status);
        Assert.Equal(createdAt, key.CreatedAtUtc);
        Assert.Null(key.AssignedSpinId);
        Assert.Null(key.ReplacesPrizeKeyId);
    }

    [Fact]
    public void CreateReplacement_WithValidData_InitializesAssignedKeyWithReplacementLink()
    {
        var prizeId = Guid.NewGuid();
        var spinId = Guid.NewGuid();
        var originalKeyId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var expiresAt = now.AddHours(2);

        var replacement = PrizeKey.CreateReplacement(
            prizeId,
            spinId,
            "REPL_HASH",
            DummyEncrypted,
            DummyNonce,
            DummyTag,
            now,
            expiresAt,
            originalKeyId);

        Assert.Equal(prizeId, replacement.PrizeId);
        Assert.Equal(spinId, replacement.AssignedSpinId);
        Assert.Equal(PrizeKeyStatus.Assigned, replacement.Status);
        Assert.Equal(now, replacement.AssignedAtUtc);
        Assert.Equal(expiresAt, replacement.ExpiresAtUtc);
        Assert.Equal(originalKeyId, replacement.ReplacesPrizeKeyId);
    }

    [Fact]
    public void CreateReplacement_WithEmptyReplacementId_ThrowsDomainException()
    {
        var ex = Assert.Throws<DomainException>(() =>
            PrizeKey.CreateReplacement(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "HASH",
                DummyEncrypted,
                DummyNonce,
                DummyTag,
                DateTime.UtcNow,
                DateTime.UtcNow.AddHours(1),
                Guid.Empty));

        Assert.Equal("PRIZE_KEY_INVALID_REPLACEMENT_ID", ex.Code);
    }

    [Fact]
    public void CreateReplacement_WithEmptySpinId_ThrowsDomainException()
    {
        var ex = Assert.Throws<DomainException>(() =>
            PrizeKey.CreateReplacement(
                Guid.NewGuid(),
                Guid.Empty,
                "HASH",
                DummyEncrypted,
                DummyNonce,
                DummyTag,
                DateTime.UtcNow,
                DateTime.UtcNow.AddHours(1),
                Guid.NewGuid()));

        Assert.Equal("PRIZE_KEY_INVALID_SPIN_ID", ex.Code);
    }

    [Fact]
    public void CreateReplacement_WithInvalidPeriod_ThrowsDomainException()
    {
        var now = DateTime.UtcNow;
        var ex = Assert.Throws<DomainException>(() =>
            PrizeKey.CreateReplacement(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "HASH",
                DummyEncrypted,
                DummyNonce,
                DummyTag,
                now,
                now.AddMinutes(-5),
                Guid.NewGuid()));

        Assert.Equal("PRIZE_KEY_INVALID_ASSIGNMENT_PERIOD", ex.Code);
    }

    [Fact]
    public void Assign_FromAvailable_SetsStatusToAssigned()
    {
        var key = new PrizeKey(Guid.NewGuid(), "HASH", DummyEncrypted, DummyNonce, DummyTag, DateTime.UtcNow);
        var spinId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var expiresAt = now.AddHours(1);

        key.Assign(spinId, now, expiresAt);

        Assert.Equal(PrizeKeyStatus.Assigned, key.Status);
        Assert.Equal(spinId, key.AssignedSpinId);
        Assert.Equal(now, key.AssignedAtUtc);
        Assert.Equal(expiresAt, key.ExpiresAtUtc);
    }

    [Fact]
    public void Redeem_FromAssigned_SetsStatusToRedeemed()
    {
        var key = new PrizeKey(Guid.NewGuid(), "HASH", DummyEncrypted, DummyNonce, DummyTag, DateTime.UtcNow);
        var spinId = Guid.NewGuid();
        var assignedAt = DateTime.UtcNow;
        var expiresAt = assignedAt.AddHours(1);
        key.Assign(spinId, assignedAt, expiresAt);

        var redeemedAt = assignedAt.AddMinutes(10);
        key.Redeem(redeemedAt);

        Assert.Equal(PrizeKeyStatus.Redeemed, key.Status);
        Assert.Equal(redeemedAt, key.RedeemedAtUtc);
    }

    [Theory]
    [InlineData(PrizeKeyStatus.Available)]
    [InlineData(PrizeKeyStatus.Redeemed)]
    [InlineData(PrizeKeyStatus.Expired)]
    [InlineData(PrizeKeyStatus.Cancelled)]
    public void Redeem_WhenNotAssigned_ThrowsDomainException(PrizeKeyStatus initialStatus)
    {
        var key = new PrizeKey(Guid.NewGuid(), "HASH", DummyEncrypted, DummyNonce, DummyTag, DateTime.UtcNow);
        if (initialStatus != PrizeKeyStatus.Available)
        {
            var assignedAt = DateTime.UtcNow.AddHours(-2);
            key.Assign(Guid.NewGuid(), assignedAt, assignedAt.AddHours(1));

            if (initialStatus == PrizeKeyStatus.Redeemed)
                key.Redeem(assignedAt.AddMinutes(10));
            else if (initialStatus == PrizeKeyStatus.Expired)
                key.Expire(assignedAt.AddHours(2));
            else if (initialStatus == PrizeKeyStatus.Cancelled)
                key.Cancel(assignedAt.AddMinutes(5));
        }

        var ex = Assert.Throws<DomainException>(() => key.Redeem(DateTime.UtcNow));
        Assert.Equal("PRIZE_KEY_CANNOT_BE_REDEEMED", ex.Code);
    }

    [Fact]
    public void Redeem_WhenExpired_ThrowsDomainException()
    {
        var key = new PrizeKey(Guid.NewGuid(), "HASH", DummyEncrypted, DummyNonce, DummyTag, DateTime.UtcNow);
        var assignedAt = DateTime.UtcNow.AddHours(-2);
        var expiresAt = assignedAt.AddHours(1);
        key.Assign(Guid.NewGuid(), assignedAt, expiresAt);

        var ex = Assert.Throws<DomainException>(() => key.Redeem(DateTime.UtcNow));
        Assert.Equal("PRIZE_KEY_ALREADY_EXPIRED", ex.Code);
    }

    [Fact]
    public void Expire_FromAssigned_SetsStatusToExpired()
    {
        var key = new PrizeKey(Guid.NewGuid(), "HASH", DummyEncrypted, DummyNonce, DummyTag, DateTime.UtcNow);
        var assignedAt = DateTime.UtcNow.AddHours(-2);
        var expiresAt = assignedAt.AddHours(1);
        key.Assign(Guid.NewGuid(), assignedAt, expiresAt);

        var expiredAt = DateTime.UtcNow;
        key.Expire(expiredAt);

        Assert.Equal(PrizeKeyStatus.Expired, key.Status);
        Assert.Equal(expiredAt, key.ExpiredAtUtc);
    }

    [Fact]
    public void Expire_BeforeExpirationTime_ThrowsDomainException()
    {
        var key = new PrizeKey(Guid.NewGuid(), "HASH", DummyEncrypted, DummyNonce, DummyTag, DateTime.UtcNow);
        var assignedAt = DateTime.UtcNow;
        var expiresAt = assignedAt.AddHours(1);
        key.Assign(Guid.NewGuid(), assignedAt, expiresAt);

        var ex = Assert.Throws<DomainException>(() => key.Expire(assignedAt.AddMinutes(30)));
        Assert.Equal("PRIZE_KEY_INVALID_EXPIRE_TIME", ex.Code);
    }

    [Fact]
    public void Cancel_FromAssigned_SetsStatusToCancelled()
    {
        var key = new PrizeKey(Guid.NewGuid(), "HASH", DummyEncrypted, DummyNonce, DummyTag, DateTime.UtcNow);
        var assignedAt = DateTime.UtcNow;
        var expiresAt = assignedAt.AddHours(1);
        key.Assign(Guid.NewGuid(), assignedAt, expiresAt);

        var cancelledAt = assignedAt.AddMinutes(15);
        key.Cancel(cancelledAt);

        Assert.Equal(PrizeKeyStatus.Cancelled, key.Status);
        Assert.Equal(cancelledAt, key.CancelledAtUtc);
    }

    [Theory]
    [InlineData(PrizeKeyStatus.Available)]
    [InlineData(PrizeKeyStatus.Redeemed)]
    [InlineData(PrizeKeyStatus.Expired)]
    [InlineData(PrizeKeyStatus.Cancelled)]
    public void Cancel_WhenNotAssigned_ThrowsDomainException(PrizeKeyStatus initialStatus)
    {
        var key = new PrizeKey(Guid.NewGuid(), "HASH", DummyEncrypted, DummyNonce, DummyTag, DateTime.UtcNow);
        if (initialStatus != PrizeKeyStatus.Available)
        {
            var assignedAt = DateTime.UtcNow.AddHours(-2);
            key.Assign(Guid.NewGuid(), assignedAt, assignedAt.AddHours(1));

            if (initialStatus == PrizeKeyStatus.Redeemed)
                key.Redeem(assignedAt.AddMinutes(10));
            else if (initialStatus == PrizeKeyStatus.Expired)
                key.Expire(assignedAt.AddHours(2));
            else if (initialStatus == PrizeKeyStatus.Cancelled)
                key.Cancel(assignedAt.AddMinutes(5));
        }

        var ex = Assert.Throws<DomainException>(() => key.Cancel(DateTime.UtcNow));
        Assert.Equal("PRIZE_KEY_CANNOT_BE_CANCELLED", ex.Code);
    }
}
