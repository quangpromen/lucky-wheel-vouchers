using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Application.Features.Admin.PrizeKeys;
using LuckyWheel.Application.Features.Public;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using LuckyWheel.Infrastructure.Persistence;
using LuckyWheel.Infrastructure.PrizeKeys;
using LuckyWheel.Infrastructure.Public;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace LuckyWheel.UnitTests.Application.Public;

public sealed class PublicWheelServiceTests
{
    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class FixedRandomProvider(long value) : IRandomNumberProvider
    {
        public int GetInt32(int fromInclusive, int toExclusive) => (int)value;
        public long GetInt64(long fromInclusive, long toExclusive) => value;
        public byte[] GetBytes(int count) => new byte[count];
    }

    private readonly IPrizeKeyProtector _protector;
    private readonly FixedClock _clock;
    private readonly DateTime _now = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);

    public PublicWheelServiceTests()
    {
        _clock = new FixedClock(_now);
        var keyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var options = Options.Create(new PrizeKeyProtectionOptions { EncryptionKey = keyBase64 });
        _protector = new AesGcmPrizeKeyProtector(options);
    }

    private sealed class RowVersionInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> SavingChanges(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
        {
            PopulateRowVersions(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            System.Threading.CancellationToken cancellationToken = default)
        {
            PopulateRowVersions(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static void PopulateRowVersions(DbContext? context)
        {
            if (context == null) return;
            foreach (var entry in context.ChangeTracker.Entries())
            {
                var prop = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "RowVersion");
                if (prop != null && (prop.CurrentValue == null || ((byte[])prop.CurrentValue).Length == 0))
                {
                    prop.CurrentValue = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
                }
            }
        }
    }

    private ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new RowVersionInterceptor())
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task GetActiveWheelBySlugAsync_ValidWheelAndActiveVersion_ReturnsPublicWheelDto()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Voucher 10%", null, null, false, 10, _now);
        db.Prizes.Add(prize);

        var seg1 = new WheelVersionPrize(version.Id, prize.Id, 500_000, 1, "#FFAA00", null, false, _now);
        var seg2 = new WheelVersionPrize(version.Id, null, 500_000, 2, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(seg1, seg2);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        var result = await service.GetActiveWheelBySlugAsync("lucky-wheel");

        Assert.NotNull(result);
        Assert.Equal(wheel.Id, result.WheelId);
        Assert.Equal("Lucky Wheel", result.Name);
        Assert.Equal("lucky-wheel", result.Slug);
        Assert.Equal(version.Id, result.VersionId);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("Voucher 10%", result.Entries[0].Label);
        Assert.Equal("Chúc bạn may mắn lần sau", result.Entries[1].Label);
    }

    [Fact]
    public async Task GetActiveWheelBySlugAsync_WheelDisabled_ThrowsNotFoundExceptionWithWheelNotAvailableCode()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        wheel.Disable(_now);
        db.Wheels.Add(wheel);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => service.GetActiveWheelBySlugAsync("lucky-wheel"));
        Assert.Equal("WHEEL_NOT_AVAILABLE", ex.Code);
    }

    [Fact]
    public async Task GetActiveWheelBySlugAsync_NoActiveVersion_ThrowsNotFoundExceptionWithWheelNotAvailableCode()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => service.GetActiveWheelBySlugAsync("lucky-wheel"));
        Assert.Equal("WHEEL_NOT_AVAILABLE", ex.Code);
    }

    [Fact]
    public async Task SpinAsync_NoPrizeOutcome_CreatesSpinHistoryWithoutWinnerLock()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 1_000_000, 1, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.Add(noPrizeSeg);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        var response = await service.SpinAsync("lucky-wheel", "idemp-test-1", new SpinRequest("player@gmail.com", true));

        Assert.NotNull(response);
        Assert.Equal("NoPrize", response.Result);
        Assert.Null(response.Prize);
        Assert.Equal("Chúc bạn may mắn lần sau", response.Message);

        var history = await db.SpinHistories.SingleOrDefaultAsync(x => x.Id == response.SpinId);
        Assert.NotNull(history);
        Assert.Equal(SpinResult.NoPrize, history.Result);

        var lockExists = await db.WinnerLocks.AnyAsync(x => x.WheelId == wheel.Id && x.EmailNormalized == "player@gmail.com");
        Assert.False(lockExists);
    }

    [Fact]
    public async Task SpinAsync_WinOutcomeWithKey_AllocatesKeyAndCreatesWinnerLock()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Voucher 500k", "500k discount", null, true, 10, _now);
        db.Prizes.Add(prize);

        var protectedKey = _protector.Protect("LW-ABCD-EFGH-1234-5678");
        var prizeKey = new PrizeKey(prize.Id, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, _now);
        db.PrizeKeys.Add(prizeKey);

        var prizeSeg = new WheelVersionPrize(version.Id, prize.Id, 1_000_000, 1, "#FFAA00", null, false, _now);
        db.WheelVersionPrizes.Add(prizeSeg);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        var response = await service.SpinAsync("lucky-wheel", "idemp-test-2", new SpinRequest("winner@gmail.com", true));

        Assert.NotNull(response);
        Assert.Equal("Win", response.Result);
        Assert.NotNull(response.Prize);
        Assert.Equal("Voucher 500k", response.Prize.Name);
        Assert.Equal("LW-ABCD-EFGH-1234-5678", response.Prize.Key);
        Assert.Equal(_now.AddMinutes(60), response.Prize.KeyExpiresAtUtc);

        var history = await db.SpinHistories.SingleOrDefaultAsync(x => x.Id == response.SpinId);
        Assert.NotNull(history);
        Assert.Equal(SpinResult.Win, history.Result);
        Assert.Equal(prizeKey.Id, history.PrizeKeyId);

        var winnerLock = await db.WinnerLocks.SingleOrDefaultAsync(x => x.WheelId == wheel.Id && x.EmailNormalized == "winner@gmail.com");
        Assert.NotNull(winnerLock);
        Assert.True(winnerLock.IsActive);
        Assert.Equal(prizeKey.Id, winnerLock.PrizeKeyId);

        var keyEntity = await db.PrizeKeys.FindAsync(prizeKey.Id);
        Assert.Equal(PrizeKeyStatus.Assigned, keyEntity!.Status);
    }

    [Fact]
    public async Task SpinAsync_ReplaySameKeySameEmail_ReturnsExactSameOutcomeWithoutDuplicateRecords()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Voucher 100k", null, null, true, 10, _now);
        db.Prizes.Add(prize);

        var protectedKey = _protector.Protect("LW-1111-2222-3333-4444");
        var prizeKey = new PrizeKey(prize.Id, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, _now);
        db.PrizeKeys.Add(prizeKey);

        var prizeSeg = new WheelVersionPrize(version.Id, prize.Id, 1_000_000, 1, "#FFAA00", null, false, _now);
        db.WheelVersionPrizes.Add(prizeSeg);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        // First spin
        var response1 = await service.SpinAsync("lucky-wheel", "idemp-replay-key", new SpinRequest("replay@gmail.com", true));

        // Replay with identical key and email
        var response2 = await service.SpinAsync("lucky-wheel", "idemp-replay-key", new SpinRequest("  REPLAY@GMAIL.COM  ", true));

        Assert.Equal(response1.SpinId, response2.SpinId);
        Assert.Equal(response1.ReceiptToken, response2.ReceiptToken);
        Assert.Equal(response1.Prize?.Key, response2.Prize?.Key);

        var count = await db.SpinHistories.CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SpinAsync_ReusedKeyWithDifferentEmail_ThrowsConflictExceptionWithIdempotencyReusedCode()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var seg = new WheelVersionPrize(version.Id, null, 1_000_000, 1, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.Add(seg);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        // First spin with player1
        await service.SpinAsync("lucky-wheel", "shared-idemp-key", new SpinRequest("player1@gmail.com", true));

        // Reused key with player2
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            service.SpinAsync("lucky-wheel", "shared-idemp-key", new SpinRequest("player2@gmail.com", true)));

        Assert.Equal("IDEMPOTENCY_KEY_REUSED", ex.Code);
    }

    [Fact]
    public async Task SpinAsync_EmailAlreadyWon_ThrowsConflictExceptionWithEmailAlreadyWonCode()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var seg = new WheelVersionPrize(version.Id, null, 1_000_000, 1, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.Add(seg);

        // Pre-existing active winner lock
        var lockObj = new WinnerLock(wheel.Id, "winner@gmail.com", Guid.NewGuid(), Guid.NewGuid(), _now);
        db.WinnerLocks.Add(lockObj);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            service.SpinAsync("lucky-wheel", "new-idemp-key", new SpinRequest("winner@gmail.com", true)));

        Assert.Equal("EMAIL_ALREADY_WON", ex.Code);
    }

    [Fact]
    public async Task SpinAsync_KeyedPrizeOutOfKeys_ExcludesFromEligibleCandidates()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var keyedPrize = new Prize(wheel.Id, "Voucher 100k", null, null, true, 10, _now);
        db.Prizes.Add(keyedPrize);

        // NO available keys in db for keyedPrize
        var keyedSeg = new WheelVersionPrize(version.Id, keyedPrize.Id, 900_000, 1, "#FFAA00", null, false, _now);
        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(keyedSeg, noPrizeSeg);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        // Even though random value is 0 (which would map to keyedSeg if it were eligible),
        // keyedSeg is excluded because it has 0 available keys, so only noPrizeSeg is eligible.
        var response = await service.SpinAsync("lucky-wheel", "idemp-out-of-stock", new SpinRequest("user@gmail.com", true));

        Assert.Equal("NoPrize", response.Result);
        Assert.Equal(noPrizeSeg.Id, response.EntryId);
    }
}
