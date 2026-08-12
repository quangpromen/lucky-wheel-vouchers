namespace LuckyWheel.UnitTests.Domain;

public class WheelVersionTests
{
    private readonly Guid _wheelId = Guid.NewGuid();
    private readonly DateTime _now = DateTime.UtcNow;

    [Fact]
    public void Constructor_ValidData_CreatesDraftVersion()
    {
        var version = new WheelVersion(_wheelId, 1, _now, _now.AddDays(1), 30, _now);
        Assert.Equal(WheelVersionStatus.Draft, version.Status);
    }

    [Fact]
    public void UpdateSchedule_DraftVersion_UpdatesSuccessfully()
    {
        var version = new WheelVersion(_wheelId, 1, _now, _now.AddDays(1), 30, _now);
        var newStart = _now.AddHours(1);
        var newEnd = _now.AddDays(2);
        
        version.UpdateSchedule(newStart, newEnd, 60, _now);

        Assert.Equal(newStart, version.StartAtUtc);
        Assert.Equal(newEnd, version.EndAtUtc);
        Assert.Equal(60, version.ClaimDurationMinutes);
    }

    [Fact]
    public void Activate_DraftVersion_TransitionsToActive()
    {
        var version = new WheelVersion(_wheelId, 1, _now, _now.AddDays(1), 30, _now);
        var adminId = Guid.NewGuid();

        version.Activate(adminId, _now);

        Assert.Equal(WheelVersionStatus.Active, version.Status);
        Assert.Equal(adminId, version.PublishedByAdminId);
    }

    [Fact]
    public void UpdateSchedule_ActiveVersion_ThrowsException()
    {
        var version = new WheelVersion(_wheelId, 1, _now, _now.AddDays(1), 30, _now);
        version.Activate(Guid.NewGuid(), _now);

        var ex = Assert.Throws<DomainException>(() => version.UpdateSchedule(_now, _now.AddDays(1), 30, _now));
        Assert.Equal("WHEEL_VERSION_CANNOT_BE_EDITED", ex.Code);
    }

    [Fact]
    public void Close_ActiveVersion_TransitionsToClosed()
    {
        var version = new WheelVersion(_wheelId, 1, _now, _now.AddDays(1), 30, _now);
        version.Activate(Guid.NewGuid(), _now);

        version.Close(_now);

        Assert.Equal(WheelVersionStatus.Closed, version.Status);
    }

    [Fact]
    public void Close_DraftVersion_ThrowsException()
    {
        var version = new WheelVersion(_wheelId, 1, _now, _now.AddDays(1), 30, _now);

        var ex = Assert.Throws<DomainException>(() => version.Close(_now));
        Assert.Equal("WHEEL_VERSION_CANNOT_BE_CLOSED", ex.Code);
    }

    [Fact]
    public void Activate_ClosedVersion_ThrowsException()
    {
        var version = new WheelVersion(_wheelId, 1, _now, _now.AddDays(1), 30, _now);
        version.Activate(Guid.NewGuid(), _now);
        version.Close(_now);

        var ex = Assert.Throws<DomainException>(() => version.Activate(Guid.NewGuid(), _now));
        Assert.Equal("WHEEL_VERSION_CANNOT_BE_ACTIVATED", ex.Code);
    }

    [Fact]
    public void Constructor_EndBeforeStart_ThrowsException()
    {
        var ex = Assert.Throws<DomainException>(() => new WheelVersion(_wheelId, 1, _now, _now.AddDays(-1), 30, _now));
        Assert.Equal("WHEEL_VERSION_INVALID_PERIOD", ex.Code);
    }
}
