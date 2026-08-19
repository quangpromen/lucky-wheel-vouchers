namespace LuckyWheel.UnitTests.Domain;

public class PrizeKeyTests
{
    private readonly Guid _prizeId = Guid.NewGuid();
    private readonly string _codeHash = "hash";
    private readonly byte[] _codeEncrypted = [1];
    private readonly byte[] _nonce = new byte[12];
    private readonly byte[] _tag = new byte[16];
    private readonly DateTime _now = DateTime.UtcNow;

    [Fact]
    public void Constructor_ValidData_CreatesAvailableKey()
    {
        var key = CreateKey();

        Assert.Equal(PrizeKeyStatus.Available, key.Status);
        Assert.Equal(_prizeId, key.PrizeId);
    }

    [Fact]
    public void Assign_AvailableKey_TransitionsToAssigned()
    {
        var key = CreateKey();
        var spinId = Guid.NewGuid();
        var expiresAt = _now.AddDays(1);

        key.Assign(spinId, _now, expiresAt);

        Assert.Equal(PrizeKeyStatus.Assigned, key.Status);
        Assert.Equal(spinId, key.AssignedSpinId);
        Assert.Equal(_now, key.AssignedAtUtc);
        Assert.Equal(expiresAt, key.ExpiresAtUtc);
    }

    [Fact]
    public void Assign_AlreadyAssignedKey_ThrowsException()
    {
        var key = CreateKey();
        var spinId = Guid.NewGuid();
        key.Assign(spinId, _now, _now.AddDays(1));

        var ex = Assert.Throws<DomainException>(() => key.Assign(spinId, _now, _now.AddDays(1)));
        Assert.Equal("PRIZE_KEY_INVALID_STATUS", ex.Code);
    }

    [Fact]
    public void Assign_ExpiresBeforeAssigned_ThrowsException()
    {
        var key = CreateKey();
        var spinId = Guid.NewGuid();

        var ex = Assert.Throws<DomainException>(() => key.Assign(spinId, _now, _now.AddDays(-1)));
        Assert.Equal("PRIZE_KEY_INVALID_ASSIGNMENT_PERIOD", ex.Code);
    }

    [Fact]
    public void Redeem_AssignedKey_TransitionsToRedeemed()
    {
        var key = CreateKey();
        var spinId = Guid.NewGuid();
        var expiresAt = _now.AddDays(1);
        key.Assign(spinId, _now, expiresAt);

        var redeemedAt = _now.AddHours(1);
        key.Redeem(redeemedAt);

        Assert.Equal(PrizeKeyStatus.Redeemed, key.Status);
        Assert.Equal(redeemedAt, key.RedeemedAtUtc);
    }

    [Fact]
    public void Redeem_AvailableKey_ThrowsException()
    {
        var key = CreateKey();
        var ex = Assert.Throws<DomainException>(() => key.Redeem(_now));
        Assert.Equal("PRIZE_KEY_CANNOT_BE_REDEEMED", ex.Code);
    }

    [Fact]
    public void Redeem_AfterExpiration_ThrowsException()
    {
        var key = CreateKey();
        var spinId = Guid.NewGuid();
        var expiresAt = _now.AddDays(1);
        key.Assign(spinId, _now, expiresAt);

        var ex = Assert.Throws<DomainException>(() => key.Redeem(expiresAt.AddSeconds(1)));
        Assert.Equal("PRIZE_KEY_ALREADY_EXPIRED", ex.Code);
    }

    [Fact]
    public void Expire_AssignedKey_TransitionsToExpired()
    {
        var key = CreateKey();
        var spinId = Guid.NewGuid();
        var expiresAt = _now.AddDays(1);
        key.Assign(spinId, _now, expiresAt);

        var expiredAt = expiresAt.AddHours(1);
        key.Expire(expiredAt);

        Assert.Equal(PrizeKeyStatus.Expired, key.Status);
        Assert.Equal(expiredAt, key.ExpiredAtUtc);
    }

    [Fact]
    public void Expire_BeforeExpiration_ThrowsException()
    {
        var key = CreateKey();
        var spinId = Guid.NewGuid();
        var expiresAt = _now.AddDays(1);
        key.Assign(spinId, _now, expiresAt);

        var ex = Assert.Throws<DomainException>(() => key.Expire(expiresAt.AddHours(-1)));
        Assert.Equal("PRIZE_KEY_INVALID_EXPIRE_TIME", ex.Code);
    }

    [Fact]
    public void Cancel_AssignedKey_TransitionsToCancelled()
    {
        var key = CreateKey();
        var spinId = Guid.NewGuid();
        var expiresAt = _now.AddDays(1);
        key.Assign(spinId, _now, expiresAt);

        var cancelledAt = _now.AddHours(1);
        key.Cancel(cancelledAt);

        Assert.Equal(PrizeKeyStatus.Cancelled, key.Status);
        Assert.Equal(cancelledAt, key.CancelledAtUtc);
    }

    [Fact]
    public void Cancel_RedeemedKey_ThrowsException()
    {
        var key = CreateKey();
        var spinId = Guid.NewGuid();
        var expiresAt = _now.AddDays(1);
        key.Assign(spinId, _now, expiresAt);
        key.Redeem(_now.AddHours(1));

        var ex = Assert.Throws<DomainException>(() => key.Cancel(_now.AddHours(2)));
        Assert.Equal("PRIZE_KEY_CANNOT_BE_CANCELLED", ex.Code);
    }

    [Fact]
    public void Available_AfterFinalState_CannotReturnToAvailable()
    {
        var key = CreateKey();
        var spinId = Guid.NewGuid();
        var expiresAt = _now.AddDays(1);
        key.Assign(spinId, _now, expiresAt);
        key.Redeem(_now.AddHours(1));

        // Attempting to assign again should throw
        var ex = Assert.Throws<DomainException>(() => key.Assign(spinId, _now, expiresAt));
        Assert.Equal("PRIZE_KEY_INVALID_STATUS", ex.Code);
    }

    private PrizeKey CreateKey() => new(_prizeId, _codeHash, _codeEncrypted, _nonce, _tag, _now);
}
