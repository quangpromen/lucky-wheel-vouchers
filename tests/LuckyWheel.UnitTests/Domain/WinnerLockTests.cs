namespace LuckyWheel.UnitTests.Domain;

public class WinnerLockTests
{
    private readonly Guid _wheelId = Guid.NewGuid();
    private readonly string _email = "test@example.com";
    private readonly Guid _spinId = Guid.NewGuid();
    private readonly Guid _prizeKeyId = Guid.NewGuid();
    private readonly DateTime _now = DateTime.UtcNow;

    [Fact]
    public void Constructor_ValidData_CreatesActiveLock()
    {
        var winnerLock = new WinnerLock(_wheelId, _email, _spinId, _prizeKeyId, _now);

        Assert.True(winnerLock.IsActive);
        Assert.False(winnerLock.IsBlocked);
    }

    [Fact]
    public void Unlock_ActiveLock_SetsIsActiveToFalse()
    {
        var winnerLock = new WinnerLock(_wheelId, _email, _spinId, _prizeKeyId, _now);
        var adminId = Guid.NewGuid();

        winnerLock.Unlock(adminId, _now);

        Assert.False(winnerLock.IsActive);
        Assert.False(winnerLock.IsBlocked);
        Assert.Equal(adminId, winnerLock.UnlockedByAdminId);
    }

    [Fact]
    public void Unlock_AlreadyUnlocked_ThrowsException()
    {
        var winnerLock = new WinnerLock(_wheelId, _email, _spinId, _prizeKeyId, _now);
        var adminId = Guid.NewGuid();
        winnerLock.Unlock(adminId, _now);

        var ex = Assert.Throws<DomainException>(() => winnerLock.Unlock(adminId, _now));
        Assert.Equal("LOCK_NOT_ACTIVE", ex.Code);
    }

    [Fact]
    public void Block_ActiveLock_KeepsActiveAndSetsBlocked()
    {
        var winnerLock = new WinnerLock(_wheelId, _email, _spinId, _prizeKeyId, _now);
        var reason = "Suspected fraud";

        winnerLock.Block(reason, _now);

        Assert.True(winnerLock.IsActive);
        Assert.True(winnerLock.IsBlocked);
        Assert.Equal(reason, winnerLock.BlockReason);
    }

    [Fact]
    public void Block_EmptyReason_ThrowsException()
    {
        var winnerLock = new WinnerLock(_wheelId, _email, _spinId, _prizeKeyId, _now);

        var ex = Assert.Throws<DomainException>(() => winnerLock.Block("", _now));
        Assert.Equal("LOCK_BLOCK_REASON_REQUIRED", ex.Code);
    }
}
