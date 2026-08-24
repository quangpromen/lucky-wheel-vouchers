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
        Assert.Equal(_wheelVersionPrizeId, spin.WheelVersionPrizeId);
        Assert.Equal(SpinStatus.Completed, spin.Status);
    }

    [Fact]
    public void CreateWin_WithKey_SetsPrizeAndKey()
    {
        var prizeId = Guid.NewGuid();
        var prizeKeyId = Guid.NewGuid();

        var spin = SpinHistory.CreateWin(_wheelId, _wheelVersionId, _wheelVersionPrizeId, _email, _email, prizeId, prizeKeyId, _idempotencyKey, _receipt, _now, _now);

        Assert.Equal(SpinResult.Win, spin.Result);
        Assert.Equal(prizeId, spin.PrizeId);
        Assert.Equal(prizeKeyId, spin.PrizeKeyId);
        Assert.Equal(_wheelVersionPrizeId, spin.WheelVersionPrizeId);
        Assert.Equal(SpinStatus.Completed, spin.Status);
    }

    [Fact]
    public void CreateWin_WithoutKey_SetsPrizeAndNullKey()
    {
        var prizeId = Guid.NewGuid();

        var spin = SpinHistory.CreateWin(_wheelId, _wheelVersionId, _wheelVersionPrizeId, _email, _email, prizeId, null, _idempotencyKey, _receipt, _now, _now);

        Assert.Equal(SpinResult.Win, spin.Result);
        Assert.Equal(prizeId, spin.PrizeId);
        Assert.Null(spin.PrizeKeyId);
        Assert.Equal(_wheelVersionPrizeId, spin.WheelVersionPrizeId);
        Assert.Equal(SpinStatus.Completed, spin.Status);
    }

    [Fact]
    public void CreateWin_MissingPrizeId_ThrowsException()
    {
        var ex = Assert.Throws<DomainException>(() => 
            SpinHistory.CreateWin(_wheelId, _wheelVersionId, _wheelVersionPrizeId, _email, _email, Guid.Empty, Guid.NewGuid(), _idempotencyKey, _receipt, _now, _now));
        
        Assert.Equal("SPIN_WIN_REQUIRES_PRIZE_ID", ex.Code);
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
        var spin = SpinHistory.CreateWin(_wheelId, _wheelVersionId, _wheelVersionPrizeId, _email, _email, Guid.NewGuid(), Guid.NewGuid(), _idempotencyKey, _receipt, _now, _now);
        var adminId = Guid.NewGuid();

        spin.Cancel(adminId, "Refund", _now);

        Assert.Equal(SpinStatus.Cancelled, spin.Status);
        Assert.Equal(adminId, spin.CancelledByAdminId);
        Assert.Equal("Refund", spin.CancellationReason);
    }

    [Fact]
    public void Cancel_AlreadyCancelled_ThrowsException()
    {
        var spin = SpinHistory.CreateWin(_wheelId, _wheelVersionId, _wheelVersionPrizeId, _email, _email, Guid.NewGuid(), Guid.NewGuid(), _idempotencyKey, _receipt, _now, _now);
        var adminId = Guid.NewGuid();
        spin.Cancel(adminId, "Refund", _now);

        var ex = Assert.Throws<DomainException>(() => spin.Cancel(adminId, "Refund", _now));
        Assert.Equal("SPIN_ALREADY_CANCELLED", ex.Code);
    }
}
