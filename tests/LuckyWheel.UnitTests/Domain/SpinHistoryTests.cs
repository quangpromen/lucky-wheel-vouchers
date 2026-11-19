using System;
using LuckyWheel.Domain.Common;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using Xunit;

namespace LuckyWheel.UnitTests.Domain;

public class SpinHistoryTests
{
    private readonly Guid _wheelId = Guid.NewGuid();
    private readonly Guid _wheelVersionId = Guid.NewGuid();
    private readonly Guid _wheelVersionPrizeId = Guid.NewGuid();
    private readonly string _email = "test@example.com";
    private readonly string _idempotencyKey = "safe-idempotency-key-123";
    private readonly string _receipt = "RC-RECEIPT-123";
    private readonly DateTime _now = DateTime.UtcNow;

    [Fact]
    public void CreateNoPrize_ValidData_CreatesWithoutPrizeKeyId()
    {
        var spin = SpinHistory.CreateNoPrize(_wheelId, _wheelVersionId, _wheelVersionPrizeId, _email, _email, _idempotencyKey, _receipt, _now, _now);

        Assert.Equal(SpinResult.NoPrize, spin.Result);
        Assert.Null(spin.PrizeKeyId);
        Assert.Null(spin.PrizeId);
        Assert.Null(spin.PrizeNameSnapshot);
        Assert.Null(spin.PrizeDescriptionSnapshot);
        Assert.Equal(_wheelVersionPrizeId, spin.WheelVersionPrizeId);
        Assert.Equal(SpinStatus.Completed, spin.Status);
    }

    [Fact]
    public void CreateWin_WithKey_SetsPrizeSnapshotAndKey()
    {
        var prizeId = Guid.NewGuid();
        var prizeKeyId = Guid.NewGuid();

        var spin = SpinHistory.CreateWin(
            _wheelId,
            _wheelVersionId,
            _wheelVersionPrizeId,
            _email,
            _email,
            prizeId,
            "  Voucher 500k  ",
            "  Áp dụng đơn 2 triệu  ",
            prizeKeyId,
            _idempotencyKey,
            _receipt,
            _now,
            _now);

        Assert.Equal(SpinResult.Win, spin.Result);
        Assert.Equal(prizeId, spin.PrizeId);
        Assert.Equal("Voucher 500k", spin.PrizeNameSnapshot);
        Assert.Equal("Áp dụng đơn 2 triệu", spin.PrizeDescriptionSnapshot);
        Assert.Equal(prizeKeyId, spin.PrizeKeyId);
        Assert.Equal(_wheelVersionPrizeId, spin.WheelVersionPrizeId);
        Assert.Equal(SpinStatus.Completed, spin.Status);
    }

    [Fact]
    public void CreateWin_WithoutKeyAndNullDescription_SetsPrizeSnapshotAndNullKey()
    {
        var prizeId = Guid.NewGuid();

        var spin = SpinHistory.CreateWin(
            _wheelId,
            _wheelVersionId,
            _wheelVersionPrizeId,
            _email,
            _email,
            prizeId,
            "Free Shipping",
            null,
            null,
            _idempotencyKey,
            _receipt,
            _now,
            _now);

        Assert.Equal(SpinResult.Win, spin.Result);
        Assert.Equal(prizeId, spin.PrizeId);
        Assert.Equal("Free Shipping", spin.PrizeNameSnapshot);
        Assert.Null(spin.PrizeDescriptionSnapshot);
        Assert.Null(spin.PrizeKeyId);
        Assert.Equal(_wheelVersionPrizeId, spin.WheelVersionPrizeId);
        Assert.Equal(SpinStatus.Completed, spin.Status);
    }

    [Fact]
    public void CreateWin_MissingPrizeId_ThrowsException()
    {
        var ex = Assert.Throws<DomainException>(() =>
            SpinHistory.CreateWin(_wheelId, _wheelVersionId, _wheelVersionPrizeId, _email, _email, Guid.Empty, "Voucher 500k", null, Guid.NewGuid(), _idempotencyKey, _receipt, _now, _now));

        Assert.Equal("SPIN_WIN_REQUIRES_PRIZE_ID", ex.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateWin_MissingPrizeNameSnapshot_ThrowsDomainException(string? invalidName)
    {
        var ex = Assert.Throws<DomainException>(() =>
            SpinHistory.CreateWin(_wheelId, _wheelVersionId, _wheelVersionPrizeId, _email, _email, Guid.NewGuid(), invalidName!, "Desc", Guid.NewGuid(), _idempotencyKey, _receipt, _now, _now));

        Assert.Equal("SPIN_WIN_REQUIRES_PRIZE_NAME_SNAPSHOT", ex.Code);
    }

    [Fact]
    public void CreateWin_TooLongPrizeNameSnapshot_ThrowsDomainException()
    {
        var tooLongName = new string('A', 201);
        var ex = Assert.Throws<DomainException>(() =>
            SpinHistory.CreateWin(_wheelId, _wheelVersionId, _wheelVersionPrizeId, _email, _email, Guid.NewGuid(), tooLongName, null, null, _idempotencyKey, _receipt, _now, _now));

        Assert.Equal("SPIN_WIN_PRIZE_NAME_SNAPSHOT_TOO_LONG", ex.Code);
    }

    [Fact]
    public void CreateWin_TooLongPrizeDescriptionSnapshot_ThrowsDomainException()
    {
        var tooLongDesc = new string('B', 1001);
        var ex = Assert.Throws<DomainException>(() =>
            SpinHistory.CreateWin(_wheelId, _wheelVersionId, _wheelVersionPrizeId, _email, _email, Guid.NewGuid(), "Voucher", tooLongDesc, null, _idempotencyKey, _receipt, _now, _now));

        Assert.Equal("SPIN_WIN_PRIZE_DESCRIPTION_SNAPSHOT_TOO_LONG", ex.Code);
    }

    [Fact]
    public void Cancel_NoPrizeSpin_ThrowsException()
    {
        var spin = SpinHistory.CreateNoPrize(_wheelId, _wheelVersionId, _wheelVersionPrizeId, _email, _email, _idempotencyKey, _receipt, _now, _now);

        var ex = Assert.Throws<DomainException>(() => spin.Cancel(Guid.NewGuid(), "Refund", _now));
        Assert.Equal("SPIN_CANNOT_CANCEL_NOPRIZE", ex.Code);
    }

    [Fact]
    public void Cancel_WinSpin_TransitionsToCancelled()
    {
        var spin = SpinHistory.CreateWin(_wheelId, _wheelVersionId, _wheelVersionPrizeId, _email, _email, Guid.NewGuid(), "Voucher 500k", null, Guid.NewGuid(), _idempotencyKey, _receipt, _now, _now);
        var adminId = Guid.NewGuid();

        spin.Cancel(adminId, "Refund", _now);

        Assert.Equal(SpinStatus.Cancelled, spin.Status);
        Assert.Equal(adminId, spin.CancelledByAdminId);
        Assert.Equal("Refund", spin.CancellationReason);
    }

    [Fact]
    public void Cancel_AlreadyCancelled_ThrowsException()
    {
        var spin = SpinHistory.CreateWin(_wheelId, _wheelVersionId, _wheelVersionPrizeId, _email, _email, Guid.NewGuid(), "Voucher 500k", null, Guid.NewGuid(), _idempotencyKey, _receipt, _now, _now);
        var adminId = Guid.NewGuid();
        spin.Cancel(adminId, "Refund", _now);

        var ex = Assert.Throws<DomainException>(() => spin.Cancel(adminId, "Refund", _now));
        Assert.Equal("SPIN_ALREADY_CANCELLED", ex.Code);
    }
}
