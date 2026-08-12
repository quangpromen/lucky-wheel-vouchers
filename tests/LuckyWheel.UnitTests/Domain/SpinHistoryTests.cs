namespace LuckyWheel.UnitTests.Domain;

public class SpinHistoryTests
{
    private readonly Guid _wheelId = Guid.NewGuid();
    private readonly Guid _wheelVersionId = Guid.NewGuid();
    private readonly string _email = "test@example.com";
    private readonly Guid _idempotencyKey = Guid.NewGuid();
    private readonly string _receipt = "receipt";
    private readonly DateTime _now = DateTime.UtcNow;

    [Fact]
    public void CreateNoPrize_ValidData_CreatesWithoutPrizeKeyId()
    {
        var spin = SpinHistory.CreateNoPrize(_wheelId, _wheelVersionId, _email, _email, _idempotencyKey, _receipt, _now);

        Assert.Equal(SpinResult.NoPrize, spin.Result);
        Assert.Null(spin.PrizeKeyId);
        Assert.Null(spin.PrizeId);
        Assert.Equal(SpinStatus.Completed, spin.Status);
    }

    [Fact]
    public void CreateWin_ValidData_RequiresPrizeAndKey()
    {
        var prizeId = Guid.NewGuid();
        var prizeKeyId = Guid.NewGuid();

        var spin = SpinHistory.CreateWin(_wheelId, _wheelVersionId, _email, _email, prizeId, prizeKeyId, _idempotencyKey, _receipt, _now);

        Assert.Equal(SpinResult.Win, spin.Result);
        Assert.Equal(prizeId, spin.PrizeId);
        Assert.Equal(prizeKeyId, spin.PrizeKeyId);
        Assert.Equal(SpinStatus.Completed, spin.Status);
    }

    [Fact]
    public void CreateWin_MissingPrizeId_ThrowsException()
    {
        var ex = Assert.Throws<DomainException>(() => 
            SpinHistory.CreateWin(_wheelId, _wheelVersionId, _email, _email, Guid.Empty, Guid.NewGuid(), _idempotencyKey, _receipt, _now));
        
        Assert.Equal("SPIN_WIN_REQUIRES_PRIZE_ID", ex.Code);
    }

    [Fact]
    public void Cancel_NoPrizeSpin_ThrowsException()
    {
        var spin = SpinHistory.CreateNoPrize(_wheelId, _wheelVersionId, _email, _email, _idempotencyKey, _receipt, _now);

        var ex = Assert.Throws<DomainException>(() => spin.Cancel(Guid.NewGuid(), "Refund", _now));
        Assert.Equal("SPIN_CANNOT_CANCEL_NOPRIZE", ex.Code);
    }

    [Fact]
    public void Cancel_WinSpin_TransitionsToCancelled()
    {
        var spin = SpinHistory.CreateWin(_wheelId, _wheelVersionId, _email, _email, Guid.NewGuid(), Guid.NewGuid(), _idempotencyKey, _receipt, _now);
        var adminId = Guid.NewGuid();

        spin.Cancel(adminId, "Refund", _now);

        Assert.Equal(SpinStatus.Cancelled, spin.Status);
        Assert.Equal(adminId, spin.CancelledByAdminId);
        Assert.Equal("Refund", spin.CancellationReason);
    }

    [Fact]
    public void Cancel_AlreadyCancelled_ThrowsException()
    {
        var spin = SpinHistory.CreateWin(_wheelId, _wheelVersionId, _email, _email, Guid.NewGuid(), Guid.NewGuid(), _idempotencyKey, _receipt, _now);
        var adminId = Guid.NewGuid();
        spin.Cancel(adminId, "Refund", _now);

        var ex = Assert.Throws<DomainException>(() => spin.Cancel(adminId, "Refund", _now));
        Assert.Equal("SPIN_ALREADY_CANCELLED", ex.Code);
    }
}
