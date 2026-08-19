using System;
using System.Linq;
using System.Threading.Tasks;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using LuckyWheel.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LuckyWheel.IntegrationTests.Persistence;

[Collection("DatabaseCollection")]
public class DatabaseConstraintTests
{
    private readonly TestDatabaseFixture _fixture;

    public DatabaseConstraintTests(TestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Can_Create_Wheel()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("Spring Campaign", $"spring-wheel-{Guid.NewGuid():N}", "Description", "Terms", DateTime.UtcNow);

        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();

        var saved = await context.Wheels.FindAsync(wheel.Id);
        Assert.NotNull(saved);
        Assert.Equal(wheel.Name, saved.Name);
    }

    [Fact]
    public async Task Cannot_Create_Two_Wheels_With_Same_Slug()
    {
        using var context = _fixture.CreateContext();
        var slug = $"unique-slug-{Guid.NewGuid():N}";

        var wheel1 = new Wheel("Wheel 1", slug, null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel1);
        await context.SaveChangesAsync();

        var wheel2 = new Wheel("Wheel 2", slug, null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel2);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Can_Create_WheelVersion_With_Valid_ForeignKey()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("Wheel for Version", $"wheel-ver-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();

        var version = new WheelVersion(wheel.Id, 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), 30, DateTime.UtcNow);
        context.WheelVersions.Add(version);
        await context.SaveChangesAsync();

        var saved = await context.WheelVersions.FindAsync(version.Id);
        Assert.NotNull(saved);
        Assert.Equal(wheel.Id, saved.WheelId);
    }

    [Fact]
    public async Task Cannot_Create_Two_WheelVersions_With_Same_VersionNumber()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("Wheel Duplicate Version", $"wheel-dup-ver-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();

        var ver1 = new WheelVersion(wheel.Id, 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), 30, DateTime.UtcNow);
        context.WheelVersions.Add(ver1);
        await context.SaveChangesAsync();

        var ver2 = new WheelVersion(wheel.Id, 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), 30, DateTime.UtcNow);
        context.WheelVersions.Add(ver2);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Cannot_Exist_Two_Active_Versions_For_Same_Wheel()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("Wheel Dual Active", $"wheel-active-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();

        var adminId = Guid.NewGuid();

        var ver1 = new WheelVersion(wheel.Id, 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), 30, DateTime.UtcNow);
        ver1.Activate(adminId, DateTime.UtcNow);
        context.WheelVersions.Add(ver1);
        await context.SaveChangesAsync();

        var ver2 = new WheelVersion(wheel.Id, 2, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), 30, DateTime.UtcNow);
        ver2.Activate(adminId, DateTime.UtcNow);
        context.WheelVersions.Add(ver2);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Cannot_Create_Two_PrizeKeys_With_Same_CodeHash()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("Wheel PrizeKey Test", $"wheel-pk-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();

        var prize = new Prize(wheel.Id, "Voucher 100k", null, null, true, 10, DateTime.UtcNow);
        context.Prizes.Add(prize);
        await context.SaveChangesAsync();

        var hash = $"hash-{Guid.NewGuid():N}";
        var key1 = TestPrizeKey(prize.Id, hash);
        context.PrizeKeys.Add(key1);
        await context.SaveChangesAsync();

        var key2 = TestPrizeKey(prize.Id, hash);
        context.PrizeKeys.Add(key2);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Cannot_Create_Two_Active_WinnerLocks_For_Same_Email()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("Wheel Lock Test", $"wheel-lock-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();

        var version = new WheelVersion(wheel.Id, 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), 30, DateTime.UtcNow);
        context.WheelVersions.Add(version);
        await context.SaveChangesAsync();

        var prize = new Prize(wheel.Id, "Prize 1", null, null, true, 10, DateTime.UtcNow);
        context.Prizes.Add(prize);
        await context.SaveChangesAsync();

        var key1 = TestPrizeKey(prize.Id, $"hash-l1-{Guid.NewGuid():N}");
        var key2 = TestPrizeKey(prize.Id, $"hash-l2-{Guid.NewGuid():N}");
        context.PrizeKeys.AddRange(key1, key2);
        await context.SaveChangesAsync();

        var email = "user@example.com";
        var spin1 = SpinHistory.CreateWin(wheel.Id, version.Id, email, email, prize.Id, key1.Id, Guid.NewGuid(), $"rcpt1-{Guid.NewGuid():N}", DateTime.UtcNow);
        var spin2 = SpinHistory.CreateWin(wheel.Id, version.Id, email, email, prize.Id, key2.Id, Guid.NewGuid(), $"rcpt2-{Guid.NewGuid():N}", DateTime.UtcNow);
        context.SpinHistories.AddRange(spin1, spin2);
        await context.SaveChangesAsync();

        var lock1 = new WinnerLock(wheel.Id, email, spin1.Id, key1.Id, DateTime.UtcNow);
        context.WinnerLocks.Add(lock1);
        await context.SaveChangesAsync();

        var lock2 = new WinnerLock(wheel.Id, email, spin2.Id, key2.Id, DateTime.UtcNow);
        context.WinnerLocks.Add(lock2);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Can_Create_Multiple_Historical_Inactive_WinnerLocks_For_Same_Email()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("Wheel Inactive Lock Test", $"wheel-inlock-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();

        var version = new WheelVersion(wheel.Id, 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), 30, DateTime.UtcNow);
        context.WheelVersions.Add(version);
        await context.SaveChangesAsync();

        var prize = new Prize(wheel.Id, "Prize 1", null, null, true, 10, DateTime.UtcNow);
        context.Prizes.Add(prize);
        await context.SaveChangesAsync();

        var key1 = TestPrizeKey(prize.Id, $"hash-in1-{Guid.NewGuid():N}");
        var key2 = TestPrizeKey(prize.Id, $"hash-in2-{Guid.NewGuid():N}");
        context.PrizeKeys.AddRange(key1, key2);
        await context.SaveChangesAsync();

        var email = "user2@example.com";
        var spin1 = SpinHistory.CreateWin(wheel.Id, version.Id, email, email, prize.Id, key1.Id, Guid.NewGuid(), $"rcpt-in1-{Guid.NewGuid():N}", DateTime.UtcNow);
        var spin2 = SpinHistory.CreateWin(wheel.Id, version.Id, email, email, prize.Id, key2.Id, Guid.NewGuid(), $"rcpt-in2-{Guid.NewGuid():N}", DateTime.UtcNow);
        context.SpinHistories.AddRange(spin1, spin2);
        await context.SaveChangesAsync();

        var adminId = Guid.NewGuid();
        var lock1 = new WinnerLock(wheel.Id, email, spin1.Id, key1.Id, DateTime.UtcNow);
        lock1.Unlock(adminId, DateTime.UtcNow); // Deactivate lock1
        context.WinnerLocks.Add(lock1);
        await context.SaveChangesAsync();

        var lock2 = new WinnerLock(wheel.Id, email, spin2.Id, key2.Id, DateTime.UtcNow);
        context.WinnerLocks.Add(lock2); // Active lock2
        await context.SaveChangesAsync();

        var count = await context.WinnerLocks.CountAsync(x => x.WheelId == wheel.Id && x.EmailNormalized == email);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Cannot_Assign_Same_PrizeKey_To_Two_Spins()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("Wheel Duplicate Key Spin", $"wheel-key-spin-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();

        var version = new WheelVersion(wheel.Id, 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), 30, DateTime.UtcNow);
        context.WheelVersions.Add(version);
        await context.SaveChangesAsync();

        var prize = new Prize(wheel.Id, "Prize 1", null, null, true, 10, DateTime.UtcNow);
        context.Prizes.Add(prize);
        await context.SaveChangesAsync();

        var key = TestPrizeKey(prize.Id, $"hash-dupkey-{Guid.NewGuid():N}");
        context.PrizeKeys.Add(key);
        await context.SaveChangesAsync();

        var spin1 = SpinHistory.CreateWin(wheel.Id, version.Id, "a@ex.com", "a@ex.com", prize.Id, key.Id, Guid.NewGuid(), $"rcpt-k1-{Guid.NewGuid():N}", DateTime.UtcNow);
        context.SpinHistories.Add(spin1);
        await context.SaveChangesAsync();

        var spin2 = SpinHistory.CreateWin(wheel.Id, version.Id, "b@ex.com", "b@ex.com", prize.Id, key.Id, Guid.NewGuid(), $"rcpt-k2-{Guid.NewGuid():N}", DateTime.UtcNow);
        context.SpinHistories.Add(spin2);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Cannot_Create_Two_Redemptions_For_Same_PrizeKey()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("Wheel Redemption Test", $"wheel-redem-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();

        var version = new WheelVersion(wheel.Id, 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), 30, DateTime.UtcNow);
        context.WheelVersions.Add(version);
        await context.SaveChangesAsync();

        var prize = new Prize(wheel.Id, "Prize 1", null, null, true, 10, DateTime.UtcNow);
        context.Prizes.Add(prize);
        await context.SaveChangesAsync();

        var key = TestPrizeKey(prize.Id, $"hash-redem-{Guid.NewGuid():N}");
        context.PrizeKeys.Add(key);
        await context.SaveChangesAsync();

        var spin1 = SpinHistory.CreateWin(wheel.Id, version.Id, "a@ex.com", "a@ex.com", prize.Id, key.Id, Guid.NewGuid(), $"rcpt-r1-{Guid.NewGuid():N}", DateTime.UtcNow);
        var spin2 = SpinHistory.CreateWin(wheel.Id, version.Id, "b@ex.com", "b@ex.com", prize.Id, key.Id, Guid.NewGuid(), $"rcpt-r2-{Guid.NewGuid():N}", DateTime.UtcNow);
        // Note: use different keys for spin2 to avoid spin key constraint
        var key2 = TestPrizeKey(prize.Id, $"hash-redem2-{Guid.NewGuid():N}");
        context.PrizeKeys.Add(key2);
        await context.SaveChangesAsync();

        var spin2Clean = SpinHistory.CreateWin(wheel.Id, version.Id, "b@ex.com", "b@ex.com", prize.Id, key2.Id, Guid.NewGuid(), $"rcpt-r2clean-{Guid.NewGuid():N}", DateTime.UtcNow);
        context.SpinHistories.AddRange(spin1, spin2Clean);
        await context.SaveChangesAsync();

        var admin = new AdminUser($"admin-{Guid.NewGuid():N}@ex.com", "Admin", DateTime.UtcNow);
        context.AdminUsers.Add(admin);
        await context.SaveChangesAsync();

        var redem1 = new PrizeRedemption(spin1.Id, key.Id, admin.Id, DateTime.UtcNow, "Note 1");
        context.PrizeRedemptions.Add(redem1);
        await context.SaveChangesAsync();

        var redem2 = new PrizeRedemption(spin2Clean.Id, key.Id, admin.Id, DateTime.UtcNow, "Note 2");
        context.PrizeRedemptions.Add(redem2);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task DeleteBehavior_Does_Not_Cascade_Delete_Historical_Data()
    {
        Guid wheelId;
        Guid versionId;

        using (var context = _fixture.CreateContext())
        {
            var wheel = new Wheel("Wheel Restrict Test", $"wheel-restr-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
            context.Wheels.Add(wheel);
            await context.SaveChangesAsync();
            wheelId = wheel.Id;

            var version = new WheelVersion(wheel.Id, 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), 30, DateTime.UtcNow);
            context.WheelVersions.Add(version);
            await context.SaveChangesAsync();
            versionId = version.Id;
        }

        using (var context2 = _fixture.CreateContext())
        {
            var wheelToDelete = await context2.Wheels.FindAsync(wheelId);
            Assert.NotNull(wheelToDelete);
            context2.Wheels.Remove(wheelToDelete);

            await Assert.ThrowsAsync<DbUpdateException>(() => context2.SaveChangesAsync());
        }

        using (var context3 = _fixture.CreateContext())
        {
            var versionStillExists = await context3.WheelVersions.AnyAsync(x => x.Id == versionId);
            Assert.True(versionStillExists);
        }
    }

    [Fact]
    public async Task Enums_Are_Saved_As_Strings_In_Database()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("Enum Test Wheel", $"wheel-enum-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();

        var version = new WheelVersion(wheel.Id, 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), 30, DateTime.UtcNow);
        context.WheelVersions.Add(version);
        await context.SaveChangesAsync();

        // Query raw string column from database to confirm string storage
        var statusStr = await context.Database.SqlQueryRaw<string>(
            "SELECT Status AS [Value] FROM WheelVersions WHERE Id = {0}", version.Id)
            .FirstOrDefaultAsync();

        Assert.Equal("Draft", statusStr);
    }

    [Fact]
    public async Task RowVersion_Changes_On_Entity_Update()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("RowVersion Wheel", $"wheel-rv-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();

        var prize = new Prize(wheel.Id, "Initial Prize", null, null, false, 5, DateTime.UtcNow);
        context.Prizes.Add(prize);
        await context.SaveChangesAsync();

        var initialRowVersion = context.Entry(prize).Property<byte[]>("RowVersion").CurrentValue;
        Assert.NotNull(initialRowVersion);

        prize.Update("Updated Prize", null, null, false, 10, DateTime.UtcNow);
        await context.SaveChangesAsync();

        var updatedRowVersion = context.Entry(prize).Property<byte[]>("RowVersion").CurrentValue;
        Assert.NotNull(updatedRowVersion);
        Assert.NotEqual(initialRowVersion, updatedRowVersion);
    }

    [Fact]
    public async Task Cannot_Create_WheelVersion_With_Missing_Wheel()
    {
        using var context = _fixture.CreateContext();
        var version = new WheelVersion(Guid.NewGuid(), 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), 30, DateTime.UtcNow);
        context.WheelVersions.Add(version);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Cannot_Create_Two_Segments_With_Same_DisplayOrder()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("Segment Constraint", $"segments-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();
        var version = new WheelVersion(wheel.Id, 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), 30, DateTime.UtcNow);
        context.WheelVersions.Add(version);
        await context.SaveChangesAsync();

        context.WheelVersionPrizes.Add(new WheelVersionPrize(version.Id, null, 1, 1, "#fff", null, true, DateTime.UtcNow));
        await context.SaveChangesAsync();
        context.WheelVersionPrizes.Add(new WheelVersionPrize(version.Id, null, 1, 1, "#000", null, true, DateTime.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Cannot_Create_Two_Spins_With_Same_IdempotencyKey()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("Idempotency Constraint", $"idempotency-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();
        var version = new WheelVersion(wheel.Id, 1, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), 30, DateTime.UtcNow);
        context.WheelVersions.Add(version);
        await context.SaveChangesAsync();
        var idempotencyKey = Guid.NewGuid();

        context.SpinHistories.Add(SpinHistory.CreateNoPrize(wheel.Id, version.Id, "one@gmail.com", "one@gmail.com", idempotencyKey, $"receipt-{Guid.NewGuid():N}", DateTime.UtcNow));
        await context.SaveChangesAsync();
        context.SpinHistories.Add(SpinHistory.CreateNoPrize(wheel.Id, version.Id, "two@gmail.com", "two@gmail.com", idempotencyKey, $"receipt-{Guid.NewGuid():N}", DateTime.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Database_Check_Constraint_Rejects_Negative_Prize_Quantity()
    {
        using var context = _fixture.CreateContext();
        var wheel = new Wheel("Check Constraint", $"checks-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
        context.Wheels.Add(wheel);
        await context.SaveChangesAsync();
        var prize = new Prize(wheel.Id, "Prize", null, null, false, 1, DateTime.UtcNow);
        context.Prizes.Add(prize);
        context.Entry(prize).Property(x => x.TotalQuantity).CurrentValue = -1;

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Concurrent_Updates_Throw_DbUpdateConcurrencyException()
    {
        Guid prizeId;
        using (var setup = _fixture.CreateContext())
        {
            var wheel = new Wheel("Concurrency", $"concurrency-{Guid.NewGuid():N}", null, "Terms", DateTime.UtcNow);
            setup.Wheels.Add(wheel);
            await setup.SaveChangesAsync();
            var prize = new Prize(wheel.Id, "Prize", null, null, false, 2, DateTime.UtcNow);
            setup.Prizes.Add(prize);
            await setup.SaveChangesAsync();
            prizeId = prize.Id;
        }

        using var first = _fixture.CreateContext();
        using var second = _fixture.CreateContext();
        var firstPrize = await first.Prizes.SingleAsync(x => x.Id == prizeId);
        var secondPrize = await second.Prizes.SingleAsync(x => x.Id == prizeId);
        firstPrize.Update("First update", null, null, false, 2, DateTime.UtcNow);
        await first.SaveChangesAsync();
        secondPrize.Update("Second update", null, null, false, 2, DateTime.UtcNow);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private static PrizeKey TestPrizeKey(Guid prizeId, string hash) =>
        new(prizeId, hash, [1], new byte[12], new byte[16], DateTime.UtcNow);
}
