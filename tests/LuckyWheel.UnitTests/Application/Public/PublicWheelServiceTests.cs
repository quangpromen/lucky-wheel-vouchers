using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
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
using Microsoft.Extensions.Logging;
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

    private sealed class RecordingPrizeSelector(IWeightedPrizeSelector inner) : IWeightedPrizeSelector
    {
        public IReadOnlyList<WheelVersionPrize>? LastCandidates { get; private set; }
        public WheelVersionPrize? LastSelected { get; private set; }

        public WheelVersionPrize Select(IReadOnlyList<WheelVersionPrize> eligibleCandidates)
        {
            LastCandidates = eligibleCandidates.ToList();
            LastSelected = inner.Select(eligibleCandidates);
            return LastSelected;
        }
    }

    private sealed class CapturingLogger : ILogger<PublicWheelService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class ThrowingUnprotectKeyProtector(IPrizeKeyProtector inner) : IPrizeKeyProtector
    {
        public string Normalize(string plaintextCode) => inner.Normalize(plaintextCode);
        public ProtectedPrizeKey Protect(string plaintextCode) => inner.Protect(plaintextCode);
        public string ComputeHash(string plaintextCode) => inner.ComputeHash(plaintextCode);
        public string Unprotect(byte[] encryptedCode, byte[] nonce, byte[] tag)
            => throw new CryptographicException("Simulated decryption failure.");
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
        Assert.Equal("Terms", result.Terms);
        Assert.Equal(version.Id, result.VersionId);
        Assert.Equal(version.VersionNumber, result.VersionNumber);
        Assert.Equal(version.StartAtUtc, result.StartAtUtc);
        Assert.Equal(version.EndAtUtc, result.EndAtUtc);
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

        var prizeSeg = new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now);
        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(prizeSeg, noPrizeSeg);
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

        var prizeSeg = new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now);
        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(prizeSeg, noPrizeSeg);
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
    public async Task SpinAsync_KeyedPrizeOutOfKeys_RemainsInDrawAndFallsBackToNoPrize()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prizeA = new Prize(wheel.Id, "Prize A", null, null, true, 10, _now);
        var prizeB = new Prize(wheel.Id, "Prize B", null, null, true, 10, _now);
        db.Prizes.AddRange(prizeA, prizeB);

        // Prize A has 0 available keys; Prize B has 1 available key
        var protectedKeyB = _protector.Protect("LW-PRIZEB-0001-0001");
        var keyB = new PrizeKey(prizeB.Id, protectedKeyB.CodeHash, protectedKeyB.EncryptedCode, protectedKeyB.EncryptionNonce, protectedKeyB.EncryptionTag, _now);
        db.PrizeKeys.Add(keyB);

        var segA = new WheelVersionPrize(version.Id, prizeA.Id, 500_000, 1, "#FFAA00", null, false, _now);
        var segB = new WheelVersionPrize(version.Id, prizeB.Id, 300_000, 2, "#00AAFF", null, false, _now);
        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 200_000, 3, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(segA, segB, noPrizeSeg);
        await db.SaveChangesAsync();

        var selector = new RecordingPrizeSelector(new WeightedPrizeSelector(new FixedRandomProvider(0)));
        var service = new PublicWheelService(db, selector, new FixedRandomProvider(0), _protector, _clock);

        var response = await service.SpinAsync("lucky-wheel", "idemp-out-of-stock", new SpinRequest("user@gmail.com", true));

        // Prize A must NOT be filtered out prior to drawing
        Assert.NotNull(selector.LastCandidates);
        Assert.Equal(3, selector.LastCandidates.Count);
        Assert.Contains(selector.LastCandidates, s => s.Id == segA.Id);
        Assert.Equal(1_000_000L, selector.LastCandidates.Sum(s => (long)s.ProbabilityWeight));
        Assert.Equal(segA.Id, selector.LastSelected?.Id);

        // Because Prize A cannot allocate a key, result falls back to NoPrize (not Prize B)
        Assert.Equal("NoPrize", response.Result);
        Assert.Null(response.Prize);
        Assert.Equal(noPrizeSeg.Id, response.EntryId);
        Assert.Equal(noPrizeSeg.DisplayOrder, response.DisplayOrder);

        // Prize B's key must remain untouched
        var storedKeyB = await db.PrizeKeys.SingleAsync(x => x.Id == keyB.Id);
        Assert.Equal(PrizeKeyStatus.Available, storedKeyB.Status);
        Assert.Null(storedKeyB.AssignedSpinId);
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

        var prizeSeg = new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now);
        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(prizeSeg, noPrizeSeg);
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

        var prizeSeg = new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now);
        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(prizeSeg, noPrizeSeg);
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

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(versionA1.Id, prizeA.Id, 900_000, 1, "#FFAA00", null, false, _now),
            new WheelVersionPrize(versionA1.Id, null, 100_000, 2, "#CCCCCC", null, true, _now),
            new WheelVersionPrize(versionB1.Id, prizeB.Id, 900_000, 1, "#FFAA00", null, false, _now),
            new WheelVersionPrize(versionB1.Id, null, 100_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        // Win on Wheel A Version 1
        await service.SpinAsync("wheel-a", "idemp-wa-v1", new SpinRequest("Quang.Dev+v1@gmail.com", true));

        // Close Version 1 and activate Version 2 on Wheel A
        versionA1.Close(_now);
        var versionA2 = new WheelVersion(wheelA.Id, 2, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        versionA2.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(versionA2);
        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(versionA2.Id, prizeA.Id, 900_000, 1, "#FFAA00", null, false, _now),
            new WheelVersionPrize(versionA2.Id, null, 100_000, 2, "#CCCCCC", null, true, _now));
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

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now),
            new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now));
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
        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now),
            new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now));
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

    [Theory]
    [InlineData(0L, false)]
    [InlineData(499_999L, false)]
    [InlineData(500_000L, true)]
    [InlineData(799_999L, true)]
    [InlineData(800_000L, false)]
    [InlineData(999_999L, false)]
    public async Task SpinAsync_PrizeAOutOfKeys_PrizeBSelectionIntervalRemainsUnchanged(long randomValue, bool expectedWinPrizeB)
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prizeA = new Prize(wheel.Id, "Prize A", "Big prize", null, true, 10, _now);
        var prizeB = new Prize(wheel.Id, "Prize B", "Medium prize", null, true, 10, _now);
        db.Prizes.AddRange(prizeA, prizeB);

        // Prize A has 0 available keys; Prize B has 1 available key
        var protectedKeyB = _protector.Protect("LW-PRIZEB-INTERVAL-1");
        var keyB = new PrizeKey(prizeB.Id, protectedKeyB.CodeHash, protectedKeyB.EncryptedCode, protectedKeyB.EncryptionNonce, protectedKeyB.EncryptionTag, _now);
        db.PrizeKeys.Add(keyB);

        // A = 500_000 [0..500_000), B = 300_000 [500_000..800_000), NoPrize = 200_000 [800_000..1_000_000)
        var segA = new WheelVersionPrize(version.Id, prizeA.Id, 500_000, 1, "#FF0000", null, false, _now);
        var segB = new WheelVersionPrize(version.Id, prizeB.Id, 300_000, 2, "#00FF00", null, false, _now);
        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 200_000, 3, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(segA, segB, noPrizeSeg);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(
            db,
            new WeightedPrizeSelector(new FixedRandomProvider(randomValue)),
            new FixedRandomProvider(randomValue),
            _protector,
            _clock);

        var response = await service.SpinAsync("lucky-wheel", $"idemp-interval-{randomValue}", new SpinRequest("player@gmail.com", true));

        var spin = await db.SpinHistories.SingleAsync();
        if (expectedWinPrizeB)
        {
            Assert.Equal("Win", response.Result);
            Assert.Equal(segB.Id, response.EntryId);
            Assert.Equal(segB.DisplayOrder, response.DisplayOrder);
            Assert.NotNull(response.Prize);
            Assert.Equal(prizeB.Id, response.Prize.Id);
            Assert.Equal("LW-PRIZEB-INTERVAL-1", response.Prize.Key);
            Assert.Equal(_now.AddMinutes(60), response.Prize.KeyExpiresAtUtc);

            Assert.Equal(SpinResult.Win, spin.Result);
            Assert.Equal(SpinStatus.Completed, spin.Status);
            Assert.Equal(prizeB.Id, spin.PrizeId);
            Assert.Equal(keyB.Id, spin.PrizeKeyId);
            Assert.Equal(segB.Id, spin.WheelVersionPrizeId);

            var storedKeyB = await db.PrizeKeys.SingleAsync(x => x.Id == keyB.Id);
            Assert.Equal(PrizeKeyStatus.Assigned, storedKeyB.Status);
            Assert.Equal(spin.Id, storedKeyB.AssignedSpinId);
            Assert.Equal(1, await db.WinnerLocks.CountAsync(x => x.WheelId == wheel.Id && x.IsActive));
        }
        else
        {
            Assert.Equal("NoPrize", response.Result);
            Assert.Null(response.Prize);
            Assert.Equal(noPrizeSeg.Id, response.EntryId);
            Assert.Equal(noPrizeSeg.DisplayOrder, response.DisplayOrder);

            Assert.Equal(SpinResult.NoPrize, spin.Result);
            Assert.Equal(SpinStatus.Completed, spin.Status);
            Assert.Null(spin.PrizeId);
            Assert.Null(spin.PrizeKeyId);
            Assert.Equal(noPrizeSeg.Id, spin.WheelVersionPrizeId);

            var storedKeyB = await db.PrizeKeys.SingleAsync(x => x.Id == keyB.Id);
            Assert.Equal(PrizeKeyStatus.Available, storedKeyB.Status);
            Assert.Null(storedKeyB.AssignedSpinId);
            Assert.Equal(0, await db.WinnerLocks.CountAsync());
        }
    }

    [Fact]
    public async Task SpinAsync_SelectedPrizeOutOfKeys_SavesAndReturnsNoPrizeWithoutWinnerLockOrKeyMutation()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prizeA = new Prize(wheel.Id, "Prize A", null, null, true, 10, _now);
        var prizeB = new Prize(wheel.Id, "Prize B", null, null, true, 10, _now);
        db.Prizes.AddRange(prizeA, prizeB);

        // Prize A only has an already-assigned key (0 Available); Prize B has an Available key
        var protectedAssignedKeyA = _protector.Protect("LW-PRIZEA-ASSIGNED-1");
        var assignedKeyA = new PrizeKey(prizeA.Id, protectedAssignedKeyA.CodeHash, protectedAssignedKeyA.EncryptedCode, protectedAssignedKeyA.EncryptionNonce, protectedAssignedKeyA.EncryptionTag, _now.AddMinutes(-30));
        var priorSpinId = Guid.NewGuid();
        assignedKeyA.Assign(priorSpinId, _now.AddMinutes(-20), _now.AddMinutes(40));

        var protectedKeyB = _protector.Protect("LW-PRIZEB-AVAIL-1");
        var keyB = new PrizeKey(prizeB.Id, protectedKeyB.CodeHash, protectedKeyB.EncryptedCode, protectedKeyB.EncryptionNonce, protectedKeyB.EncryptionTag, _now);
        db.PrizeKeys.AddRange(assignedKeyA, keyB);

        var segA = new WheelVersionPrize(version.Id, prizeA.Id, 500_000, 1, "#FF0000", null, false, _now);
        var segB = new WheelVersionPrize(version.Id, prizeB.Id, 300_000, 2, "#00FF00", null, false, _now);
        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 200_000, 3, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(segA, segB, noPrizeSeg);
        await db.SaveChangesAsync();

        var logger = new CapturingLogger();
        var service = new PublicWheelService(
            db,
            new WeightedPrizeSelector(new FixedRandomProvider(123_456)),
            new FixedRandomProvider(123_456),
            _protector,
            _clock,
            logger);

        const string rawEmail = "Player.Secret+tag@gmail.com";
        const string idempKey = "idemp-fallback-secret-1";

        var response = await service.SpinAsync("lucky-wheel", idempKey, new SpinRequest(rawEmail, true));

        // Response must match NoPrize segment of same version
        Assert.Equal("NoPrize", response.Result);
        Assert.Null(response.Prize);
        Assert.Equal(noPrizeSeg.Id, response.EntryId);
        Assert.Equal(noPrizeSeg.DisplayOrder, response.DisplayOrder);
        Assert.Equal("Chúc bạn may mắn lần sau", response.Message);

        // Database must contain exactly one NoPrize Completed spin with no PrizeId, PrizeKeyId, or WinnerLock
        var spin = await db.SpinHistories.SingleAsync();
        Assert.Equal(response.SpinId, spin.Id);
        Assert.Equal(SpinResult.NoPrize, spin.Result);
        Assert.Equal(SpinStatus.Completed, spin.Status);
        Assert.Null(spin.PrizeId);
        Assert.Null(spin.PrizeKeyId);
        Assert.Equal(noPrizeSeg.Id, spin.WheelVersionPrizeId);
        Assert.Equal(0, await db.WinnerLocks.CountAsync());

        // Keys must not be assigned or mutated
        var storedKeyA = await db.PrizeKeys.SingleAsync(x => x.Id == assignedKeyA.Id);
        Assert.Equal(PrizeKeyStatus.Assigned, storedKeyA.Status);
        Assert.Equal(priorSpinId, storedKeyA.AssignedSpinId);

        var storedKeyB = await db.PrizeKeys.SingleAsync(x => x.Id == keyB.Id);
        Assert.Equal(PrizeKeyStatus.Available, storedKeyB.Status);
        Assert.Null(storedKeyB.AssignedSpinId);

        // Safe diagnostic log must use only IDs and safe reason (no email, key, receipt, or idempotency key)
        var logEntry = Assert.Single(logger.Messages);
        Assert.Contains(wheel.Id.ToString(), logEntry);
        Assert.Contains(version.Id.ToString(), logEntry);
        Assert.Contains(segA.Id.ToString(), logEntry);
        Assert.Contains(prizeA.Id.ToString(), logEntry);
        Assert.Contains(noPrizeSeg.Id.ToString(), logEntry);
        Assert.Contains("NO_AVAILABLE_PRIZE_KEY", logEntry);
        Assert.DoesNotContain("player", logEntry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gmail", logEntry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(idempKey, logEntry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(response.ReceiptToken, logEntry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LW-PRIZE", logEntry, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpinAsync_DirectNoPrizeSelectionInMultiSegmentWheel_SavesNoPrizeWithoutTouchingKeys()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prizeA = new Prize(wheel.Id, "Prize A", null, null, true, 10, _now);
        var prizeB = new Prize(wheel.Id, "Prize B", null, null, true, 10, _now);
        db.Prizes.AddRange(prizeA, prizeB);

        var protectedKeyA = _protector.Protect("LW-DIRECT-NP-A");
        var keyA = new PrizeKey(prizeA.Id, protectedKeyA.CodeHash, protectedKeyA.EncryptedCode, protectedKeyA.EncryptionNonce, protectedKeyA.EncryptionTag, _now);
        var protectedKeyB = _protector.Protect("LW-DIRECT-NP-B");
        var keyB = new PrizeKey(prizeB.Id, protectedKeyB.CodeHash, protectedKeyB.EncryptedCode, protectedKeyB.EncryptionNonce, protectedKeyB.EncryptionTag, _now);
        db.PrizeKeys.AddRange(keyA, keyB);

        var segA = new WheelVersionPrize(version.Id, prizeA.Id, 500_000, 1, "#FF0000", null, false, _now);
        var segB = new WheelVersionPrize(version.Id, prizeB.Id, 300_000, 2, "#00FF00", null, false, _now);
        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 200_000, 3, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(segA, segB, noPrizeSeg);
        await db.SaveChangesAsync();

        var logger = new CapturingLogger();
        var service = new PublicWheelService(
            db,
            new WeightedPrizeSelector(new FixedRandomProvider(850_000)),
            new FixedRandomProvider(850_000),
            _protector,
            _clock,
            logger);

        var response = await service.SpinAsync("lucky-wheel", "idemp-direct-noprize", new SpinRequest("player@gmail.com", true));

        Assert.Equal("NoPrize", response.Result);
        Assert.Null(response.Prize);
        Assert.Equal(noPrizeSeg.Id, response.EntryId);
        Assert.Equal(noPrizeSeg.DisplayOrder, response.DisplayOrder);

        var spin = await db.SpinHistories.SingleAsync();
        Assert.Equal(SpinResult.NoPrize, spin.Result);
        Assert.Equal(SpinStatus.Completed, spin.Status);
        Assert.Null(spin.PrizeId);
        Assert.Null(spin.PrizeKeyId);
        Assert.Equal(noPrizeSeg.Id, spin.WheelVersionPrizeId);
        Assert.Equal(0, await db.WinnerLocks.CountAsync());
        Assert.Equal(2, await db.PrizeKeys.CountAsync(x => x.Status == PrizeKeyStatus.Available));
        Assert.Empty(logger.Messages);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(500_000L)]
    [InlineData(800_000L)]
    public async Task SpinAsync_AllKeyedPrizesOutOfStock_StillSavesAndReturnsNoPrize(long randomValue)
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prizeA = new Prize(wheel.Id, "Prize A", null, null, true, 10, _now);
        var prizeB = new Prize(wheel.Id, "Prize B", null, null, true, 10, _now);
        db.Prizes.AddRange(prizeA, prizeB);

        // Neither Prize A nor Prize B has any Available keys
        var segA = new WheelVersionPrize(version.Id, prizeA.Id, 500_000, 1, "#FF0000", null, false, _now);
        var segB = new WheelVersionPrize(version.Id, prizeB.Id, 300_000, 2, "#00FF00", null, false, _now);
        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 200_000, 3, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(segA, segB, noPrizeSeg);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(
            db,
            new WeightedPrizeSelector(new FixedRandomProvider(randomValue)),
            new FixedRandomProvider(randomValue),
            _protector,
            _clock);

        var response = await service.SpinAsync("lucky-wheel", $"idemp-all-empty-{randomValue}", new SpinRequest("player@gmail.com", true));

        Assert.Equal("NoPrize", response.Result);
        Assert.Null(response.Prize);
        Assert.Equal(noPrizeSeg.Id, response.EntryId);
        Assert.Equal(noPrizeSeg.DisplayOrder, response.DisplayOrder);

        var spin = await db.SpinHistories.SingleAsync();
        Assert.Equal(SpinResult.NoPrize, spin.Result);
        Assert.Equal(SpinStatus.Completed, spin.Status);
        Assert.Null(spin.PrizeId);
        Assert.Null(spin.PrizeKeyId);
        Assert.Equal(noPrizeSeg.Id, spin.WheelVersionPrizeId);
        Assert.Equal(0, await db.WinnerLocks.CountAsync());
    }

    [Fact]
    public async Task SpinAsync_RetrySameIdempotencyKeyAfterKeyReplenished_ReturnsOriginalNoPrizeWithoutRespinning()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prizeA = new Prize(wheel.Id, "Prize A", null, null, true, 10, _now);
        db.Prizes.Add(prizeA);

        var segA = new WheelVersionPrize(version.Id, prizeA.Id, 800_000, 1, "#FF0000", null, false, _now);
        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 200_000, 2, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(segA, noPrizeSeg);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(
            db,
            new WeightedPrizeSelector(new FixedRandomProvider(0)),
            new FixedRandomProvider(0),
            _protector,
            _clock);

        // First spin hits Prize A while out of keys -> falls back to NoPrize
        var firstResponse = await service.SpinAsync("lucky-wheel", "idemp-replenish-retry", new SpinRequest("player@gmail.com", true));
        Assert.Equal("NoPrize", firstResponse.Result);
        Assert.Equal(noPrizeSeg.Id, firstResponse.EntryId);
        Assert.Equal(noPrizeSeg.DisplayOrder, firstResponse.DisplayOrder);

        // Replenish stock for Prize A
        var protectedNewKey = _protector.Protect("LW-REPLENISHED-0001");
        var replenishedKey = new PrizeKey(prizeA.Id, protectedNewKey.CodeHash, protectedNewKey.EncryptedCode, protectedNewKey.EncryptionNonce, protectedNewKey.EncryptionTag, _now.AddMinutes(1));
        db.PrizeKeys.Add(replenishedKey);
        await db.SaveChangesAsync();

        // Retry with the exact same idempotency key must replay the stored NoPrize spin without re-spinning
        var replayResponse = await service.SpinAsync("lucky-wheel", "idemp-replenish-retry", new SpinRequest("player@gmail.com", true));

        Assert.Equal(firstResponse.SpinId, replayResponse.SpinId);
        Assert.Equal(firstResponse.ReceiptToken, replayResponse.ReceiptToken);
        Assert.Equal("NoPrize", replayResponse.Result);
        Assert.Null(replayResponse.Prize);
        Assert.Equal(noPrizeSeg.Id, replayResponse.EntryId);
        Assert.Equal(noPrizeSeg.DisplayOrder, replayResponse.DisplayOrder);

        Assert.Equal(1, await db.SpinHistories.CountAsync());
        Assert.Equal(0, await db.WinnerLocks.CountAsync());

        var storedNewKey = await db.PrizeKeys.SingleAsync(x => x.Id == replenishedKey.Id);
        Assert.Equal(PrizeKeyStatus.Available, storedNewKey.Status);
        Assert.Null(storedNewKey.AssignedSpinId);
    }

    [Fact]
    public async Task SpinAsync_NoSegmentsConfigured_ThrowsInvalidOperationExceptionWithoutFallback()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-invalid-empty", new SpinRequest("player@gmail.com", true)));

        Assert.Equal(0, await db.SpinHistories.CountAsync());
        Assert.Equal(0, await db.WinnerLocks.CountAsync());
    }

    [Theory]
    [InlineData(500_000, 200_000)]
    [InlineData(600_000, 500_000)]
    public async Task SpinAsync_TotalWeightNotOneMillion_ThrowsInvalidOperationExceptionWithoutFallback(int prizeWeight, int noPrizeWeight)
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Prize A", null, null, true, 10, _now);
        db.Prizes.Add(prize);
        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, prize.Id, prizeWeight, 1, "#FF0000", null, false, _now),
            new WheelVersionPrize(version.Id, null, noPrizeWeight, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SpinAsync("lucky-wheel", $"idemp-bad-weight-{prizeWeight}", new SpinRequest("player@gmail.com", true)));

        Assert.Equal(0, await db.SpinHistories.CountAsync());
        Assert.Equal(0, await db.WinnerLocks.CountAsync());
    }

    [Fact]
    public async Task SpinAsync_MissingNoPrizeSegment_ThrowsInvalidOperationExceptionWithoutFallback()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Prize A", null, null, true, 10, _now);
        db.Prizes.Add(prize);
        db.WheelVersionPrizes.Add(new WheelVersionPrize(version.Id, prize.Id, 1_000_000, 1, "#FF0000", null, false, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-no-noprize", new SpinRequest("player@gmail.com", true)));

        Assert.Equal(0, await db.SpinHistories.CountAsync());
        Assert.Equal(0, await db.WinnerLocks.CountAsync());
    }

    [Fact]
    public async Task SpinAsync_MultipleNoPrizeSegments_ThrowsInvalidOperationExceptionWithoutFallback()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, null, 500_000, 1, "#CCCCCC", null, true, _now),
            new WheelVersionPrize(version.Id, null, 500_000, 2, "#DDDDDD", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-multi-noprize", new SpinRequest("player@gmail.com", true)));

        Assert.Equal(0, await db.SpinHistories.CountAsync());
        Assert.Equal(0, await db.WinnerLocks.CountAsync());
    }

    [Fact]
    public async Task SpinAsync_DisabledPrizeReferencedInActiveVersion_ThrowsInvalidOperationExceptionWithoutFallback()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var disabledPrize = new Prize(wheel.Id, "Disabled Prize", null, null, true, 10, _now);
        disabledPrize.Disable(_now);
        db.Prizes.Add(disabledPrize);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, disabledPrize.Id, 500_000, 1, "#FF0000", null, false, _now),
            new WheelVersionPrize(version.Id, null, 500_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-disabled-prize", new SpinRequest("player@gmail.com", true)));

        Assert.Equal(0, await db.SpinHistories.CountAsync());
        Assert.Equal(0, await db.WinnerLocks.CountAsync());
    }

    [Fact]
    public async Task SpinAsync_MissingPrizeReferencedInActiveVersion_ThrowsInvalidOperationExceptionWithoutFallback()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, Guid.NewGuid(), 500_000, 1, "#FF0000", null, false, _now),
            new WheelVersionPrize(version.Id, null, 500_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-missing-prize", new SpinRequest("player@gmail.com", true)));

        Assert.Equal(0, await db.SpinHistories.CountAsync());
        Assert.Equal(0, await db.WinnerLocks.CountAsync());
    }

    [Fact]
    public async Task SpinAsync_CrossWheelPrizeReferencedInActiveVersion_ThrowsInvalidOperationExceptionWithoutFallback()
    {
        using var db = CreateContext();
        var wheelA = new Wheel("Wheel A", "wheel-a", "Description", "Terms", _now);
        var wheelB = new Wheel("Wheel B", "wheel-b", "Description", "Terms", _now);
        db.Wheels.AddRange(wheelA, wheelB);

        var versionA = new WheelVersion(wheelA.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        versionA.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(versionA);

        var foreignPrize = new Prize(wheelB.Id, "Foreign Prize", null, null, true, 10, _now);
        db.Prizes.Add(foreignPrize);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(versionA.Id, foreignPrize.Id, 500_000, 1, "#FF0000", null, false, _now),
            new WheelVersionPrize(versionA.Id, null, 500_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SpinAsync("wheel-a", "idemp-cross-wheel-prize", new SpinRequest("player@gmail.com", true)));

        Assert.Equal(0, await db.SpinHistories.CountAsync());
        Assert.Equal(0, await db.WinnerLocks.CountAsync());
    }

    [Fact]
    public async Task SpinAsync_KeyDecryptionFailure_PropagatesErrorWithoutFallbackToNoPrize()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Prize A", null, null, true, 10, _now);
        db.Prizes.Add(prize);

        var protectedKey = _protector.Protect("LW-CORRUPTED-KEY-1");
        var prizeKey = new PrizeKey(prize.Id, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, _now);
        db.PrizeKeys.Add(prizeKey);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, prize.Id, 500_000, 1, "#FF0000", null, false, _now),
            new WheelVersionPrize(version.Id, null, 500_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var failingProtector = new ThrowingUnprotectKeyProtector(_protector);
        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), failingProtector, _clock);

        await Assert.ThrowsAsync<CryptographicException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-decrypt-error", new SpinRequest("player@gmail.com", true)));

        Assert.Equal(0, await db.SpinHistories.CountAsync());
        Assert.Equal(0, await db.WinnerLocks.CountAsync());

        var storedKey = await db.PrizeKeys.SingleAsync(x => x.Id == prizeKey.Id);
        Assert.Equal(PrizeKeyStatus.Available, storedKey.Status);
        Assert.Null(storedKey.AssignedSpinId);
    }

    private sealed class ConditionalSaveFaultInterceptor(Func<Exception?> faultFactory)
        : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var ex = faultFactory();
            if (ex != null)
            {
                throw ex;
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private ApplicationDbContext CreateContextWithFault(Func<Exception?> faultFactory)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new RowVersionInterceptor(), new ConditionalSaveFaultInterceptor(faultFactory))
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task SpinAsync_ReplayAfterWheelDisabledVersionClosedAndWinnerLockActive_ReturnsOriginalWinAndNoPrize()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Voucher 500k", null, null, true, 10, _now);
        db.Prizes.Add(prize);

        var prizeSeg = new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FF0000", null, false, _now);
        var noPrizeSeg = new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(prizeSeg, noPrizeSeg);
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        // First spin falls back to NoPrize (0 keys)
        var originalNoPrize = await service.SpinAsync("lucky-wheel", "idemp-closed-np", new SpinRequest("Quang.Dev+1@gmail.com", true));
        Assert.Equal("NoPrize", originalNoPrize.Result);

        // Replenish key, then Win creates active WinnerLock
        var protectedKey = _protector.Protect("LW-CLOSED-REPLAY-1");
        var prizeKey = new PrizeKey(prize.Id, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, _now);
        db.PrizeKeys.Add(prizeKey);
        await db.SaveChangesAsync();

        var originalWin = await service.SpinAsync("lucky-wheel", "idemp-closed-win", new SpinRequest("quangdev+2@gmail.com", true));
        Assert.Equal("Win", originalWin.Result);
        Assert.Equal("LW-CLOSED-REPLAY-1", originalWin.Prize?.Key);

        // Close version and disable wheel
        version.Close(_now.AddMinutes(5));
        wheel.Disable(_now.AddMinutes(5));
        await db.SaveChangesAsync();

        // Replay both NoPrize and Win using Gmail alias variants
        var replayNoPrize = await service.SpinAsync("lucky-wheel", "idemp-closed-np", new SpinRequest("q.u.a.n.g.d.e.v+retry@GMAIL.COM", true));
        var replayWin = await service.SpinAsync("lucky-wheel", "idemp-closed-win", new SpinRequest("  Quang.Dev+retry2@gmail.com ", true));

        Assert.Equal(originalNoPrize.SpinId, replayNoPrize.SpinId);
        Assert.Equal(originalNoPrize.ReceiptToken, replayNoPrize.ReceiptToken);
        Assert.Equal("NoPrize", replayNoPrize.Result);
        Assert.Equal(noPrizeSeg.Id, replayNoPrize.EntryId);

        Assert.Equal(originalWin.SpinId, replayWin.SpinId);
        Assert.Equal(originalWin.ReceiptToken, replayWin.ReceiptToken);
        Assert.Equal("Win", replayWin.Result);
        Assert.Equal("LW-CLOSED-REPLAY-1", replayWin.Prize?.Key);

        Assert.Equal(2, await db.SpinHistories.CountAsync());
        Assert.Equal(1, await db.WinnerLocks.CountAsync());
        Assert.Equal(PrizeKeyStatus.Assigned, (await db.PrizeKeys.SingleAsync()).Status);
    }

    [Fact]
    public async Task SpinAsync_KeyDecryptionFailure_CleansAttemptStateWithoutClearingUnrelatedTrackedEntity()
    {
        using var db = CreateContext();
        var unrelatedWheel = new Wheel("Unrelated", "unrelated-wheel", null, "Terms", _now);
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.AddRange(unrelatedWheel, wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Prize A", null, null, true, 10, _now);
        db.Prizes.Add(prize);

        var protectedKey = _protector.Protect("LW-CORRUPTED-KEY-2");
        var prizeKey = new PrizeKey(prize.Id, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, _now);
        db.PrizeKeys.Add(prizeKey);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, prize.Id, 500_000, 1, "#FF0000", null, false, _now),
            new WheelVersionPrize(version.Id, null, 500_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        Assert.Equal(EntityState.Unchanged, db.Entry(unrelatedWheel).State);

        var failingProtector = new ThrowingUnprotectKeyProtector(_protector);
        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), failingProtector, _clock);

        await Assert.ThrowsAsync<CryptographicException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-decrypt-cleanup", new SpinRequest("player@gmail.com", true)));

        Assert.Equal(EntityState.Unchanged, db.Entry(unrelatedWheel).State);
        Assert.Empty(db.ChangeTracker.Entries<SpinHistory>());
        Assert.Empty(db.ChangeTracker.Entries<WinnerLock>());
        Assert.Equal(PrizeKeyStatus.Available, prizeKey.Status);
        Assert.Null(prizeKey.AssignedSpinId);
    }

    [Fact]
    public async Task SpinAsync_NonUniqueDbUpdateExceptionOnSave_PropagatesAndCleansUpAttemptStateAndRevertsKey()
    {
        var shouldFailSave = false;
        using var db = CreateContextWithFault(() =>
            shouldFailSave
                ? new DbUpdateException("Simulated non-unique failure", new InvalidOperationException("FK/check failure"))
                : null);

        var unrelatedWheel = new Wheel("Unrelated", "unrelated-wheel", null, "Terms", _now);
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.AddRange(unrelatedWheel, wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Prize A", null, null, true, 10, _now);
        db.Prizes.Add(prize);

        var protectedKey = _protector.Protect("LW-NONUNIQUE-REVERT-1");
        var prizeKey = new PrizeKey(prize.Id, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, _now);
        db.PrizeKeys.Add(prizeKey);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FF0000", null, false, _now),
            new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var logger = new CapturingLogger();
        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock, logger);

        shouldFailSave = true;
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-nonunique-err", new SpinRequest("Secret.Player+tag@gmail.com", true)));

        Assert.IsNotType<ConflictException>(ex);
        Assert.Equal(EntityState.Unchanged, db.Entry(unrelatedWheel).State);
        Assert.Empty(db.ChangeTracker.Entries<SpinHistory>());
        Assert.Empty(db.ChangeTracker.Entries<WinnerLock>());
        Assert.Equal(EntityState.Detached, db.Entry(prizeKey).State);
        Assert.Equal(PrizeKeyStatus.Available, prizeKey.Status);
        Assert.Null(prizeKey.AssignedSpinId);

        // Subsequent save on the same DbContext must not persist dirty entities from the failed attempt
        shouldFailSave = false;
        db.AuditLogs.Add(new AuditLog(null, AuditAction.Updated, "Wheel", unrelatedWheel.Id, "Subsequent write", null, _now));
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.SpinHistories.CountAsync());
        Assert.Equal(0, await db.WinnerLocks.CountAsync());
        var storedKey = await db.PrizeKeys.SingleAsync(x => x.Id == prizeKey.Id);
        Assert.Equal(PrizeKeyStatus.Available, storedKey.Status);
        Assert.Null(storedKey.AssignedSpinId);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task SpinAsync_DbUpdateConcurrencyExceptionOnSave_PropagatesAndCleansUpAttemptState()
    {
        var shouldFailSave = false;
        using var db = CreateContextWithFault(() =>
            shouldFailSave ? new DbUpdateConcurrencyException("Simulated concurrency mismatch") : null);

        var unrelatedWheel = new Wheel("Unrelated", "unrelated-wheel", null, "Terms", _now);
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.AddRange(unrelatedWheel, wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Prize A", null, null, true, 10, _now);
        db.Prizes.Add(prize);

        var protectedKey = _protector.Protect("LW-CONCURRENCY-ERR-1");
        var prizeKey = new PrizeKey(prize.Id, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, _now);
        db.PrizeKeys.Add(prizeKey);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FF0000", null, false, _now),
            new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        shouldFailSave = true;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-concurrency-err", new SpinRequest("player@gmail.com", true)));

        Assert.Equal(EntityState.Unchanged, db.Entry(unrelatedWheel).State);
        Assert.Empty(db.ChangeTracker.Entries<SpinHistory>());
        Assert.Empty(db.ChangeTracker.Entries<WinnerLock>());
        Assert.Equal(EntityState.Detached, db.Entry(prizeKey).State);
        Assert.Equal(PrizeKeyStatus.Available, prizeKey.Status);
        Assert.Null(prizeKey.AssignedSpinId);

        shouldFailSave = false;
        db.AuditLogs.Add(new AuditLog(null, AuditAction.Updated, "Wheel", unrelatedWheel.Id, "Follow-up write", null, _now));
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.SpinHistories.CountAsync());
        Assert.Equal(0, await db.WinnerLocks.CountAsync());
        var storedKey = await db.PrizeKeys.SingleAsync(x => x.Id == prizeKey.Id);
        Assert.Equal(PrizeKeyStatus.Available, storedKey.Status);
    }

    [Fact]
    public async Task SpinAsync_CancellationDuringSave_PropagatesOperationCanceledAndCleansAttemptState()
    {
        using var cts = new CancellationTokenSource();
        var cancelOnSave = false;
        using var db = CreateContextWithFault(() =>
        {
            if (cancelOnSave)
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }

            return null;
        });

        var unrelatedWheel = new Wheel("Unrelated", "unrelated-wheel", null, "Terms", _now);
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.AddRange(unrelatedWheel, wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Prize A", null, null, true, 10, _now);
        db.Prizes.Add(prize);

        var protectedKey = _protector.Protect("LW-CANCEL-REVERT-1");
        var prizeKey = new PrizeKey(prize.Id, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, _now);
        db.PrizeKeys.Add(prizeKey);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FF0000", null, false, _now),
            new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        cancelOnSave = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SpinAsync("lucky-wheel", "idemp-cancel-err", new SpinRequest("cancel@gmail.com", true), cts.Token));

        Assert.Equal(EntityState.Unchanged, db.Entry(unrelatedWheel).State);
        Assert.Empty(db.ChangeTracker.Entries<SpinHistory>());
        Assert.Empty(db.ChangeTracker.Entries<WinnerLock>());
        Assert.Equal(EntityState.Detached, db.Entry(prizeKey).State);
        Assert.Equal(PrizeKeyStatus.Available, prizeKey.Status);
        Assert.Null(prizeKey.AssignedSpinId);

        cancelOnSave = false;
        db.AuditLogs.Add(new AuditLog(null, AuditAction.Updated, "Wheel", unrelatedWheel.Id, "Post-cancel write", null, _now));
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.SpinHistories.CountAsync());
        Assert.Equal(0, await db.WinnerLocks.CountAsync());
    }

    private sealed class MutableClock(DateTime initialUtcNow) : IClock
    {
        private DateTime _current = initialUtcNow;
        public DateTimeOffset UtcNow => new(_current, TimeSpan.Zero);
        public void AdvanceTo(DateTime utcNow) => _current = utcNow;
    }

    private sealed class AdvancingClockSelector(
        IWeightedPrizeSelector inner,
        Action onSelect) : IWeightedPrizeSelector
    {
        public WheelVersionPrize Select(IReadOnlyList<WheelVersionPrize> eligibleCandidates)
        {
            onSelect();
            return inner.Select(eligibleCandidates);
        }
    }

    [Fact]
    public async Task SpinAsync_ClockAdvancesBeforeKeyAllocation_ComputesKeyExpirationFromAssignmentTimeAndPreservesAcceptedTermsAtUtc()
    {
        using var db = CreateContext();
        var t0RequestStart = _now;
        var t1Allocation = _now.AddMinutes(3);
        var endAtUtc = _now.AddMinutes(5);
        const int claimDurationMinutes = 60;

        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddMinutes(-10), endAtUtc, claimDurationMinutes, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Voucher 500k", null, null, true, 10, _now);
        db.Prizes.Add(prize);

        var protectedKey = _protector.Protect("LW-ASSIGN-TIME-0001");
        var prizeKey = new PrizeKey(prize.Id, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, _now);
        db.PrizeKeys.Add(prizeKey);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FF0000", null, false, _now),
            new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var mutableClock = new MutableClock(t0RequestStart);
        var selector = new AdvancingClockSelector(
            new WeightedPrizeSelector(new FixedRandomProvider(0)),
            () => mutableClock.AdvanceTo(t1Allocation));

        var service = new PublicWheelService(db, selector, new FixedRandomProvider(0), _protector, mutableClock);

        var response = await service.SpinAsync("lucky-wheel", "idemp-assign-time", new SpinRequest("player@gmail.com", true));

        var expectedExpiresAtUtc = t1Allocation.AddMinutes(claimDurationMinutes);
        Assert.Equal("Win", response.Result);
        Assert.Equal(expectedExpiresAtUtc, response.Prize?.KeyExpiresAtUtc);
        Assert.Equal(t1Allocation, response.CompletedAtUtc);

        var spin = await db.SpinHistories.SingleAsync();
        Assert.Equal(t0RequestStart, spin.AcceptedTermsAtUtc);
        Assert.Equal(t1Allocation, spin.CreatedAtUtc);

        var storedKey = await db.PrizeKeys.SingleAsync();
        Assert.Equal(t1Allocation, storedKey.AssignedAtUtc);
        Assert.Equal(expectedExpiresAtUtc, storedKey.ExpiresAtUtc);

        // Advance past EndAtUtc and verify replay still returns original Win with identical deadline
        mutableClock.AdvanceTo(endAtUtc.AddMinutes(30));
        var replay = await service.SpinAsync("lucky-wheel", "idemp-assign-time", new SpinRequest("player@gmail.com", true));
        Assert.Equal(response.SpinId, replay.SpinId);
        Assert.Equal(response.ReceiptToken, replay.ReceiptToken);
        Assert.Equal(expectedExpiresAtUtc, replay.Prize?.KeyExpiresAtUtc);
        Assert.Equal("LW-ASSIGN-TIME-0001", replay.Prize?.Key);
    }

    [Fact]
    public async Task SpinAsync_WinStoresPrizeSnapshot_ReplayPreservesOriginalAfterPrizeUpdateAndNewWinUsesUpdatedPrize()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Voucher 500k", "Áp dụng đơn 2 triệu", null, true, 10, _now);
        db.Prizes.Add(prize);

        var protectedKey1 = _protector.Protect("LW-SNAP-0001-AAAA");
        var key1 = new PrizeKey(prize.Id, protectedKey1.CodeHash, protectedKey1.EncryptedCode, protectedKey1.EncryptionNonce, protectedKey1.EncryptionTag, _now);
        var protectedKey2 = _protector.Protect("LW-SNAP-0002-BBBB");
        var key2 = new PrizeKey(prize.Id, protectedKey2.CodeHash, protectedKey2.EncryptedCode, protectedKey2.EncryptionNonce, protectedKey2.EncryptionTag, _now.AddSeconds(1));
        db.PrizeKeys.AddRange(key1, key2);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now),
            new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        var firstWin = await service.SpinAsync("lucky-wheel", "idemp-snap-1", new SpinRequest("winner1@gmail.com", true));

        Assert.Equal("Win", firstWin.Result);
        Assert.NotNull(firstWin.Prize);
        Assert.Equal("Voucher 500k", firstWin.Prize!.Name);
        Assert.Equal("Áp dụng đơn 2 triệu", firstWin.Prize.Description);
        Assert.Equal("LW-SNAP-0001-AAAA", firstWin.Prize.Key);

        var storedFirstSpin = await db.SpinHistories.SingleAsync(x => x.Id == firstWin.SpinId);
        Assert.Equal("Voucher 500k", storedFirstSpin.PrizeNameSnapshot);
        Assert.Equal("Áp dụng đơn 2 triệu", storedFirstSpin.PrizeDescriptionSnapshot);

        // Admin updates prize name and description
        prize.Update("Voucher 300k", "Áp dụng đơn 1 triệu", null, true, 10, _now.AddMinutes(5));
        await db.SaveChangesAsync();

        // Replay of first spin must still return the original snapshot and key
        var replayFirst = await service.SpinAsync("lucky-wheel", "idemp-snap-1", new SpinRequest("winner1@gmail.com", true));
        Assert.Equal(firstWin.SpinId, replayFirst.SpinId);
        Assert.Equal(firstWin.ReceiptToken, replayFirst.ReceiptToken);
        Assert.NotNull(replayFirst.Prize);
        Assert.Equal(prize.Id, replayFirst.Prize!.Id);
        Assert.Equal("Voucher 500k", replayFirst.Prize.Name);
        Assert.Equal("Áp dụng đơn 2 triệu", replayFirst.Prize.Description);
        Assert.Equal("LW-SNAP-0001-AAAA", replayFirst.Prize.Key);
        Assert.Equal(firstWin.Prize.KeyExpiresAtUtc, replayFirst.Prize.KeyExpiresAtUtc);

        // New win by another email uses the updated prize details
        var secondWin = await service.SpinAsync("lucky-wheel", "idemp-snap-2", new SpinRequest("winner2@gmail.com", true));
        Assert.Equal("Win", secondWin.Result);
        Assert.NotNull(secondWin.Prize);
        Assert.Equal("Voucher 300k", secondWin.Prize!.Name);
        Assert.Equal("Áp dụng đơn 1 triệu", secondWin.Prize.Description);
        Assert.Equal("LW-SNAP-0002-BBBB", secondWin.Prize.Key);

        var storedSecondSpin = await db.SpinHistories.SingleAsync(x => x.Id == secondWin.SpinId);
        Assert.Equal("Voucher 300k", storedSecondSpin.PrizeNameSnapshot);
        Assert.Equal("Áp dụng đơn 1 triệu", storedSecondSpin.PrizeDescriptionSnapshot);
    }

    [Fact]
    public async Task SpinAsync_WinWithNullDescription_ReplayKeepsNullDescriptionAfterPrizeDescriptionAdded()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Quà không mô tả", null, null, false, 10, _now);
        db.Prizes.Add(prize);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now),
            new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);

        var win = await service.SpinAsync("lucky-wheel", "idemp-null-desc", new SpinRequest("nulldesc@gmail.com", true));
        Assert.Equal("Win", win.Result);
        Assert.Equal("Quà không mô tả", win.Prize!.Name);
        Assert.Null(win.Prize.Description);

        var storedSpin = await db.SpinHistories.SingleAsync(x => x.Id == win.SpinId);
        Assert.Equal("Quà không mô tả", storedSpin.PrizeNameSnapshot);
        Assert.Null(storedSpin.PrizeDescriptionSnapshot);

        prize.Update("Quà đã đổi tên", "Mô tả mới thêm sau", null, false, 10, _now.AddMinutes(5));
        await db.SaveChangesAsync();

        var replay = await service.SpinAsync("lucky-wheel", "idemp-null-desc", new SpinRequest("nulldesc@gmail.com", true));
        Assert.Equal("Quà không mô tả", replay.Prize!.Name);
        Assert.Null(replay.Prize.Description);
    }

    [Fact]
    public async Task SpinAsync_DirectAndFallbackNoPrize_PersistNullPrizeSnapshots()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prizeOutOfStock = new Prize(wheel.Id, "Voucher Hết Mã", "Mô tả", null, true, 10, _now);
        db.Prizes.Add(prizeOutOfStock);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, prizeOutOfStock.Id, 500_000, 1, "#FFAA00", null, false, _now),
            new WheelVersionPrize(version.Id, null, 500_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        // Fallback NoPrize (draws Prize out of stock)
        var fallbackSvc = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);
        var fallbackRes = await fallbackSvc.SpinAsync("lucky-wheel", "idemp-np-fallback", new SpinRequest("np1@gmail.com", true));
        Assert.Equal("NoPrize", fallbackRes.Result);
        Assert.Null(fallbackRes.Prize);

        // Direct NoPrize (draws NoPrize segment directly)
        var directSvc = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(750_000)), new FixedRandomProvider(750_000), _protector, _clock);
        var directRes = await directSvc.SpinAsync("lucky-wheel", "idemp-np-direct", new SpinRequest("np2@gmail.com", true));
        Assert.Equal("NoPrize", directRes.Result);
        Assert.Null(directRes.Prize);

        var spins = await db.SpinHistories.ToListAsync();
        Assert.Equal(2, spins.Count);
        Assert.All(spins, s =>
        {
            Assert.Equal(SpinResult.NoPrize, s.Result);
            Assert.Null(s.PrizeNameSnapshot);
            Assert.Null(s.PrizeDescriptionSnapshot);
        });
    }

    [Fact]
    public async Task SpinAsync_LegacyWinWithoutSnapshot_ReplayUsesFixedNeutralFallbackWithoutReadingCurrentPrize()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Tên giải hiện tại đã sửa", "Mô tả hiện tại đã sửa", null, true, 10, _now);
        db.Prizes.Add(prize);

        var segPrize = new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now);
        var segNoPrize = new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now);
        db.WheelVersionPrizes.AddRange(segPrize, segNoPrize);

        var protectedKey = _protector.Protect("LW-LEGACY-0001-ABCD");
        var prizeKey = new PrizeKey(prize.Id, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, _now.AddDays(-1));
        var legacySpin = SpinHistory.CreateWin(
            wheel.Id,
            version.Id,
            segPrize.Id,
            "legacy@gmail.com",
            "legacy@gmail.com",
            prize.Id,
            "Temporary",
            "Temporary Desc",
            prizeKey.Id,
            "idemp-legacy-win",
            "RC-LEGACY-0001",
            _now.AddHours(-2),
            _now.AddHours(-2));
        prizeKey.Assign(legacySpin.Id, _now.AddHours(-2), _now.AddHours(-1));

        db.PrizeKeys.Add(prizeKey);
        db.SpinHistories.Add(legacySpin);
        // Simulate pre-migration legacy database row with NULL snapshot columns
        db.Entry(legacySpin).Property(x => x.PrizeNameSnapshot).CurrentValue = null;
        db.Entry(legacySpin).Property(x => x.PrizeDescriptionSnapshot).CurrentValue = null;
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);
        var replay = await service.SpinAsync("lucky-wheel", "idemp-legacy-win", new SpinRequest("legacy@gmail.com", true));

        Assert.Equal(legacySpin.Id, replay.SpinId);
        Assert.Equal("Win", replay.Result);
        Assert.NotNull(replay.Prize);
        Assert.Equal(prize.Id, replay.Prize!.Id);
        Assert.Equal(SpinHistory.LegacyMissingPrizeNameFallback, replay.Prize.Name);
        Assert.Null(replay.Prize.Description);
        Assert.Equal("LW-LEGACY-0001-ABCD", replay.Prize.Key);
        Assert.Equal(_now.AddHours(-1), replay.Prize.KeyExpiresAtUtc);
    }

    [Fact]
    public async Task SpinAsync_WinWithReplacementKeyCreated_ReplayStillReturnsOriginalKeyAndSnapshot()
    {
        using var db = CreateContext();
        var wheel = new Wheel("Lucky Wheel", "lucky-wheel", "Description", "Terms", _now);
        db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _now.AddDays(-1), _now.AddDays(7), 60, _now);
        version.Activate(Guid.NewGuid(), _now);
        db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Voucher 500k", "Áp dụng đơn 2 triệu", null, true, 10, _now);
        db.Prizes.Add(prize);

        var protectedOrig = _protector.Protect("LW-ORIG-KEY-0001");
        var origKey = new PrizeKey(prize.Id, protectedOrig.CodeHash, protectedOrig.EncryptedCode, protectedOrig.EncryptionNonce, protectedOrig.EncryptionTag, _now);
        db.PrizeKeys.Add(origKey);

        db.WheelVersionPrizes.AddRange(
            new WheelVersionPrize(version.Id, prize.Id, 900_000, 1, "#FFAA00", null, false, _now),
            new WheelVersionPrize(version.Id, null, 100_000, 2, "#CCCCCC", null, true, _now));
        await db.SaveChangesAsync();

        var service = new PublicWheelService(db, new WeightedPrizeSelector(new FixedRandomProvider(0)), new FixedRandomProvider(0), _protector, _clock);
        var win = await service.SpinAsync("lucky-wheel", "idemp-repl-check", new SpinRequest("winner@gmail.com", true));
        Assert.Equal("LW-ORIG-KEY-0001", win.Prize!.Key);

        // Simulate Stage 9 cancel + replacement key + prize rename
        var storedOrigKey = await db.PrizeKeys.SingleAsync(x => x.Id == origKey.Id);
        storedOrigKey.Cancel(_now.AddMinutes(10));
        var protectedRepl = _protector.Protect("LW-REPL-KEY-9999");
        var replKey = PrizeKey.CreateReplacement(
            prize.Id,
            win.SpinId,
            protectedRepl.CodeHash,
            protectedRepl.EncryptedCode,
            protectedRepl.EncryptionNonce,
            protectedRepl.EncryptionTag,
            _now.AddMinutes(15),
            _now.AddMinutes(75),
            origKey.Id);
        db.PrizeKeys.Add(replKey);
        prize.Update("Voucher Renamed", "Mô tả khác", null, true, 10, _now.AddMinutes(20));
        await db.SaveChangesAsync();

        // Replay must keep reading original linked PrizeKey and original snapshot (never auto-switch to replacement key)
        var replay = await service.SpinAsync("lucky-wheel", "idemp-repl-check", new SpinRequest("winner@gmail.com", true));
        Assert.Equal(win.SpinId, replay.SpinId);
        Assert.Equal("Voucher 500k", replay.Prize!.Name);
        Assert.Equal("Áp dụng đơn 2 triệu", replay.Prize.Description);
        Assert.Equal("LW-ORIG-KEY-0001", replay.Prize.Key);
        Assert.Equal(_now.AddMinutes(60), replay.Prize.KeyExpiresAtUtc);
    }
}
