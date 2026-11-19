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

    [Fact]
    public async Task SpinAsync_WinWithGmailVariant_PreservesEmailOriginalAndNormalizesIdentity()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Voucher 500k", null, null, true, 10, _now);
        db.Prizes.Add(prize);

        var protectedKey = _protector.Protect("LW-AAAA-BBBB-CCCC-0001");
        var prizeKey = new PrizeKey(prize.Id, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, _now);
        db.PrizeKeys.Add(prizeKey);

        var prizeSeg = new WheelVersionPrize(version.Id, prize.Id, 1_000_000, 1, "#FFAA00", null, false, _now);
        db.WheelVersionPrizes.Add(prizeSeg);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        var response = await service.SpinAsync(
            "lucky-wheel",
            "idemp-variant-win",
            new SpinRequest("  Quang.Dev+campaign@Gmail.COM  ", true));

        Assert.Equal("Win", response.Result);

        var history = await db.SpinHistories.SingleAsync(x => x.Id == response.SpinId);
        Assert.Equal("Quang.Dev+campaign@Gmail.COM", history.EmailOriginal);
        Assert.Equal("quangdev@gmail.com", history.EmailNormalized);

        var winnerLock = await db.WinnerLocks.SingleAsync(x => x.WheelId == wheel.Id);
        Assert.Equal("quangdev@gmail.com", winnerLock.EmailNormalized);
        Assert.True(winnerLock.IsActive);
    }

    [Theory]
    [InlineData("quangdev@gmail.com")]
    [InlineData("q.u.a.n.g.d.e.v@gmail.com")]
    [InlineData("QUANGDEV+other@GMAIL.COM")]
    [InlineData("  quang.dev+promo.2026@gmail.com  ")]
    public async Task SpinAsync_SameGmailVariantsAfterWin_BlockedByWinnerLock(string variantEmail)
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Voucher 100k", null, null, false, 10, _now);
        db.Prizes.Add(prize);

        var prizeSeg = new WheelVersionPrize(version.Id, prize.Id, 1_000_000, 1, "#FFAA00", null, false, _now);
        db.WheelVersionPrizes.Add(prizeSeg);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        // First spin wins using dotted +tag variant
        await service.SpinAsync("lucky-wheel", "idemp-first-win", new SpinRequest("Quang.Dev+campaign@gmail.com", true));

        // Subsequent spin using another variant of the same Gmail must fail with EMAIL_ALREADY_WON
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-second-attempt", new SpinRequest(variantEmail, true)));

        Assert.Equal("EMAIL_ALREADY_WON", ex.Code);
        Assert.Equal(1, await db.SpinHistories.CountAsync());
        Assert.Equal(1, await db.WinnerLocks.CountAsync());
    }

    [Fact]
    public async Task SpinAsync_SameGmailVariantAcrossVersionsAndWheels_RemainsLockedInSameWheelAndAllowedInDifferentWheel()
    {
        using var db = CreateContext();
        var wheelA = new Wheel("Wheel A", "wheel-a", null, "Terms", _now);
        var wheelB = new Wheel("Wheel B", "wheel-b", null, "Terms", _now);
        db.Wheels.AddRange(wheelA, wheelB);

        var versionA1 = new WheelVersion(wheelA.Id, 1, _now.AddDays(-2), _now.AddDays(7), 60, _now);
        versionA1.Activate(Guid.NewGuid(), _now);
        var versionB1 = new WheelVersion(wheelB.Id, 1, _now.AddDays(-2), _now.AddDays(7), 60, _now);
        versionB1.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.AddRange(versionA1, versionB1);

        var prizeA = new Prize(wheelA.Id, "Gift A", null, null, false, 10, _now);
        var prizeB = new Prize(wheelB.Id, "Gift B", null, null, false, 10, _now);
        db.Prizes.AddRange(prizeA, prizeB);

        db.WheelVersionPrizes.Add(new WheelVersionPrize(versionA1.Id, prizeA.Id, 1_000_000, 1, "#FFAA00", null, false, _now));
        db.WheelVersionPrizes.Add(new WheelVersionPrize(versionB1.Id, prizeB.Id, 1_000_000, 1, "#FFAA00", null, false, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        // Win on Wheel A Version 1
        await service.SpinAsync("wheel-a", "idemp-wa-v1", new SpinRequest("Quang.Dev+v1@gmail.com", true));

        // Close Version 1 and activate Version 2 on Wheel A
        versionA1.Close(_now);
        var versionA2 = new WheelVersion(wheelA.Id, 2, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        versionA2.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(versionA2);
        db.WheelVersionPrizes.Add(new WheelVersionPrize(versionA2.Id, prizeA.Id, 1_000_000, 1, "#FFAA00", null, false, _now));
        await db.SaveChangesAsync();

        // Variant must still be locked on Wheel A Version 2
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            service.SpinAsync("wheel-a", "idemp-wa-v2", new SpinRequest("quangdev@gmail.com", true)));
        Assert.Equal("EMAIL_ALREADY_WON", ex.Code);

        // Variant must be allowed on Wheel B
        var wheelBResult = await service.SpinAsync("wheel-b", "idemp-wb-v1", new SpinRequest("q.u.a.n.g.d.e.v+wb@GMAIL.COM", true));
        Assert.Equal("Win", wheelBResult.Result);
    }

    [Fact]
    public async Task SpinAsync_NoPrizeWithGmailVariants_DoesNotLockAndPreservesEachEmailOriginal()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        db.WheelVersionPrizes.Add(new WheelVersionPrize(version.Id, null, 1_000_000, 1, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        var spin1 = await service.SpinAsync("lucky-wheel", "idemp-np-1", new SpinRequest("Quang.Dev+one@gmail.com", true));
        var spin2 = await service.SpinAsync("lucky-wheel", "idemp-np-2", new SpinRequest("quangdev+two@GMAIL.COM", true));

        Assert.Equal("NoPrize", spin1.Result);
        Assert.Equal("NoPrize", spin2.Result);
        Assert.False(await db.WinnerLocks.AnyAsync());

        var h1 = await db.SpinHistories.SingleAsync(x => x.Id == spin1.SpinId);
        var h2 = await db.SpinHistories.SingleAsync(x => x.Id == spin2.SpinId);
        Assert.Equal("Quang.Dev+one@gmail.com", h1.EmailOriginal);
        Assert.Equal("quangdev@gmail.com", h1.EmailNormalized);
        Assert.Equal("quangdev+two@GMAIL.COM", h2.EmailOriginal);
        Assert.Equal("quangdev@gmail.com", h2.EmailNormalized);
    }

    [Fact]
    public async Task SpinAsync_ReplaySameKeyWithSameGmailVariant_ReturnsOriginalOutcomeWithoutDuplicate()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Voucher 100k", null, null, true, 10, _now);
        db.Prizes.Add(prize);

        var protectedKey = _protector.Protect("LW-9999-8888-7777-6666");
        var prizeKey = new PrizeKey(prize.Id, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, _now);
        db.PrizeKeys.Add(prizeKey);

        db.WheelVersionPrizes.Add(new WheelVersionPrize(version.Id, prize.Id, 1_000_000, 1, "#FFAA00", null, false, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        var response1 = await service.SpinAsync("lucky-wheel", "idemp-variant-replay", new SpinRequest("Quang.Dev+first@gmail.com", true));
        var response2 = await service.SpinAsync("lucky-wheel", "idemp-variant-replay", new SpinRequest("  q.u.a.n.g.d.e.v+retry@GMAIL.COM  ", true));

        Assert.Equal(response1.SpinId, response2.SpinId);
        Assert.Equal(response1.ReceiptToken, response2.ReceiptToken);
        Assert.Equal(response1.Prize?.Key, response2.Prize?.Key);
        Assert.Equal(1, await db.SpinHistories.CountAsync());

        var storedSpin = await db.SpinHistories.SingleAsync();
        Assert.Equal("Quang.Dev+first@gmail.com", storedSpin.EmailOriginal);
        Assert.Equal("quangdev@gmail.com", storedSpin.EmailNormalized);
    }

    [Theory]
    [InlineData(".user@gmail.com")]
    [InlineData("user.@gmail.com")]
    [InlineData("user..name@gmail.com")]
    [InlineData("+tag@gmail.com")]
    [InlineData("user+@gmail.com")]
    [InlineData("user@googlemail.com")]
    [InlineData("user@yahoo.com")]
    [InlineData("\u212A@gmail.com")]
    [InlineData("user\u200B@gmail.com")]
    public async Task SpinAsync_InvalidGmail_ThrowsValidationExceptionWithoutSavingSpin(string invalidEmail)
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);
        db.WheelVersionPrizes.Add(new WheelVersionPrize(version.Id, null, 1_000_000, 1, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-invalid-email", new SpinRequest(invalidEmail, true)));

        Assert.True(ex.Errors.ContainsKey("email"));
        Assert.Equal(0, await db.SpinHistories.CountAsync());
        Assert.Equal(0, await db.WinnerLocks.CountAsync());
    }

    [Fact]
    public async Task SpinAsync_ReplaySameKeyWithDifferentNormalizedGmail_ThrowsIdempotencyKeyReused()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);
        db.WheelVersionPrizes.Add(new WheelVersionPrize(version.Id, null, 1_000_000, 1, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        await service.SpinAsync("lucky-wheel", "idemp-diff-norm", new SpinRequest("quang.dev+1@gmail.com", true));

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-diff-norm", new SpinRequest("quang_dev@gmail.com", true)));

        Assert.Equal("IDEMPOTENCY_KEY_REUSED", ex.Code);
        Assert.Equal(1, await db.SpinHistories.CountAsync());
    }

    [Fact]
    public async Task SpinAsync_UnlockedWinnerLock_AllowsAnotherGmailVariantToSpinAndRelock()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Voucher 100k", null, null, false, 10, _now);
        db.Prizes.Add(prize);
        db.WheelVersionPrizes.Add(new WheelVersionPrize(version.Id, prize.Id, 1_000_000, 1, "#FFAA00", null, false, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        await service.SpinAsync("lucky-wheel", "idemp-unlock-1", new SpinRequest("Quang.Dev+first@gmail.com", true));

        var existingLock = await db.WinnerLocks.SingleAsync(x => x.WheelId == wheel.Id && x.IsActive);
        existingLock.Unlock(Guid.NewGuid(), _now.AddMinutes(5));
        await db.SaveChangesAsync();

        var secondWin = await service.SpinAsync("lucky-wheel", "idemp-unlock-2", new SpinRequest("q.u.a.n.g.d.e.v+second@GMAIL.COM", true));
        Assert.Equal("Win", secondWin.Result);
        Assert.Equal(1, await db.WinnerLocks.CountAsync(x => x.WheelId == wheel.Id && x.IsActive));

        var blockedThird = await Assert.ThrowsAsync<ConflictException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-unlock-3", new SpinRequest("quangdev@gmail.com", true)));
        Assert.Equal("EMAIL_ALREADY_WON", blockedThird.Code);
    }

    [Fact]
    public async Task SpinAsync_BlockedWinnerLock_BlocksAllVariantsOfSameGmail()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);
        db.WheelVersionPrizes.Add(new WheelVersionPrize(version.Id, null, 1_000_000, 1, "#CCCCCC", null, true, _now));

        var blockedLock = new WinnerLock(wheel.Id, "quangdev@gmail.com", Guid.NewGuid(), null, _now);
        blockedLock.Block("Suspected abuse", _now);
        db.WinnerLocks.Add(blockedLock);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-blocked-variant", new SpinRequest("  Quang.Dev+alias@GMAIL.COM  ", true)));

        Assert.Equal("EMAIL_ALREADY_WON", ex.Code);
        Assert.Equal(0, await db.SpinHistories.CountAsync());
    }
}
