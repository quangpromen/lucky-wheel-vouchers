using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Application.Common.Authentication;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Application.Common.Validation;
using LuckyWheel.Application.Features.Admin.PrizeKeys;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using LuckyWheel.Infrastructure.Persistence;
using LuckyWheel.Infrastructure.PrizeKeys;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LuckyWheel.UnitTests.Application.PrizeKeys;

public sealed class PrizeKeyLifecycleServiceTests
{
    private readonly ApplicationDbContext _db;
    private readonly IPrizeKeyGenerator _generator;
    private readonly IPrizeKeyProtector _protector;
    private readonly MutableClock _clock;
    private readonly TestAdminContext _adminContext;
    private readonly PrizeKeyService _service;
    private readonly PrizeKeyExpirationProcessor _expirationProcessor;

    public PrizeKeyLifecycleServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new RowVersionInterceptor())
            .Options;

        _db = new ApplicationDbContext(options);
        _generator = new CryptoPrizeKeyGenerator();
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _protector = new AesGcmPrizeKeyProtector(Options.Create(new PrizeKeyProtectionOptions { EncryptionKey = key }));
        _clock = new MutableClock(new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        _adminContext = new TestAdminContext(Guid.NewGuid());

        _service = new PrizeKeyService(_db, _generator, _protector, _clock, _adminContext);
        _expirationProcessor = new PrizeKeyExpirationProcessor(_db, _clock, NullLogger<PrizeKeyExpirationProcessor>.Instance);
    }

    private async Task<(Wheel Wheel, WheelVersion Version, Prize Prize, SpinHistory Spin, PrizeKey Key)> SeedAssignedKeyAsync()
    {
        var now = _clock.UtcNow.UtcDateTime;
        var wheel = new Wheel("Hè Rực Rỡ", "he-ruc-ro", null, "Terms", now);
        _db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, now.AddDays(-1), now.AddDays(10), 60, now);
        version.Activate(_adminContext.AdminId!.Value, now);
        _db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Voucher 500k", null, null, requiresKey: true, totalQuantity: 10, now);
        _db.Prizes.Add(prize);

        var protectedKey = _protector.Protect("LW-AAAA-BBBB-CCCC-DDDD");
        var prizeKey = new PrizeKey(prize.Id, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, now);
        _db.PrizeKeys.Add(prizeKey);

        var segmentId = Guid.NewGuid();
        var spin = SpinHistory.CreateWin(
            wheel.Id,
            version.Id,
            segmentId,
            "nguyenvanquang@gmail.com",
            "nguyenvanquang@gmail.com",
            prize.Id,
            prize.Name,
            prize.Description,
            prizeKey.Id,
            "idemp-12345",
            "RC-TOKEN-12345",
            now,
            now);
        _db.SpinHistories.Add(spin);

        prizeKey.Assign(spin.Id, now, now.AddMinutes(60));

        var winnerLock = new WinnerLock(wheel.Id, "nguyenvanquang@gmail.com", spin.Id, prizeKey.Id, now);
        _db.WinnerLocks.Add(winnerLock);

        await _db.SaveChangesAsync();
        return (wheel, version, prize, spin, prizeKey);
    }

    // ── 1. Redeem Tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RedeemKeyAsync_ValidAssignedKey_SucceedsAndCreatesRedemptionAndAudit()
    {
        var (_, _, _, spin, prizeKey) = await SeedAssignedKeyAsync();

        var response = await _service.RedeemKeyAsync(prizeKey.Id, new RedeemPrizeKeyRequest("Giao quà thành công"));

        Assert.Equal(prizeKey.Id, response.PrizeKeyId);
        Assert.Equal(spin.Id, response.SpinId);
        Assert.Equal("Redeemed", response.Status);
        Assert.Equal("Giao quà thành công", response.Note);

        var updatedKey = await _db.PrizeKeys.FindAsync(prizeKey.Id);
        Assert.NotNull(updatedKey);
        Assert.Equal(PrizeKeyStatus.Redeemed, updatedKey.Status);
        Assert.NotNull(updatedKey.RedeemedAtUtc);

        var redemption = await _db.PrizeRedemptions.SingleOrDefaultAsync(x => x.PrizeKeyId == prizeKey.Id);
        Assert.NotNull(redemption);
        Assert.Equal(spin.Id, redemption.SpinId);
        Assert.Equal(_adminContext.AdminId, redemption.ConfirmedByAdminId);
        Assert.Equal("Giao quà thành công", redemption.Note);

        var audit = await _db.AuditLogs.SingleOrDefaultAsync(x => x.EntityType == "PrizeKey" && x.EntityId == prizeKey.Id && x.Action == AuditAction.Redeemed);
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task RedeemKeyAsync_KeyNotFound_ThrowsNotFoundException()
    {
        var nonExistentId = Guid.NewGuid();
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.RedeemKeyAsync(nonExistentId, new RedeemPrizeKeyRequest(null)));
    }

    [Fact]
    public async Task RedeemKeyAsync_WhenKeyAvailable_ThrowsConflictException()
    {
        var prizeId = Guid.NewGuid();
        var protectedKey = _protector.Protect("LW-TEST-1234-5678-AAAA");
        var key = new PrizeKey(prizeId, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, DateTime.UtcNow);
        _db.PrizeKeys.Add(key);
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            _service.RedeemKeyAsync(key.Id, new RedeemPrizeKeyRequest(null)));

        Assert.Equal("PRIZE_KEY_NOT_ASSIGNED", ex.Code);
    }

    [Fact]
    public async Task RedeemKeyAsync_WhenKeyExpired_ThrowsConflictException()
    {
        var (_, _, _, _, prizeKey) = await SeedAssignedKeyAsync();

        // Advance time past expiration
        _clock.Set(prizeKey.ExpiresAtUtc!.Value.AddMinutes(5));

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            _service.RedeemKeyAsync(prizeKey.Id, new RedeemPrizeKeyRequest(null)));

        Assert.Equal("PRIZE_KEY_EXPIRED", ex.Code);
    }

    // ── 2. Cancel Tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelKeyAsync_ValidAssignedKey_SucceedsAndCreatesAudit()
    {
        var (_, _, _, spin, prizeKey) = await SeedAssignedKeyAsync();

        var response = await _service.CancelKeyAsync(prizeKey.Id, new CancelPrizeKeyRequest("Gian lận thông tin"));

        Assert.Equal(prizeKey.Id, response.PrizeKeyId);
        Assert.Equal(spin.Id, response.SpinId);
        Assert.Equal("Cancelled", response.Status);
        Assert.Equal("Gian lận thông tin", response.Reason);

        var updatedKey = await _db.PrizeKeys.FindAsync(prizeKey.Id);
        Assert.NotNull(updatedKey);
        Assert.Equal(PrizeKeyStatus.Cancelled, updatedKey.Status);
        Assert.NotNull(updatedKey.CancelledAtUtc);

        var audit = await _db.AuditLogs.SingleOrDefaultAsync(x => x.EntityType == "PrizeKey" && x.EntityId == prizeKey.Id && x.Action == AuditAction.Cancelled);
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task CancelKeyAsync_EmptyReason_ThrowsValidationException()
    {
        var (_, _, _, _, prizeKey) = await SeedAssignedKeyAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.CancelKeyAsync(prizeKey.Id, new CancelPrizeKeyRequest("")));
    }

    [Fact]
    public async Task CancelKeyAsync_WhenKeyNotAssigned_ThrowsConflictException()
    {
        var prizeId = Guid.NewGuid();
        var protectedKey = _protector.Protect("LW-TEST-1234-5678-BBBB");
        var key = new PrizeKey(prizeId, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, DateTime.UtcNow);
        _db.PrizeKeys.Add(key);
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            _service.CancelKeyAsync(key.Id, new CancelPrizeKeyRequest("Reason")));

        Assert.Equal("PRIZE_KEY_NOT_ASSIGNED", ex.Code);
    }

    // ── 3. Replacement Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateReplacementKeyAsync_FromCancelledKey_CreatesAssignedReplacementKey()
    {
        var (wheel, version, prize, spin, prizeKey) = await SeedAssignedKeyAsync();

        // First cancel the key
        await _service.CancelKeyAsync(prizeKey.Id, new CancelPrizeKeyRequest("Huỷ hợp lệ"));

        var response = await _service.CreateReplacementKeyAsync(prizeKey.Id);

        Assert.NotEqual(prizeKey.Id, response.NewPrizeKeyId);
        Assert.Equal(prizeKey.Id, response.OriginalPrizeKeyId);
        Assert.Equal(prize.Id, response.PrizeId);
        Assert.Equal(spin.Id, response.SpinId);
        Assert.Equal("Assigned", response.Status);
        Assert.NotNull(response.Code);

        var replacement = await _db.PrizeKeys.FindAsync(response.NewPrizeKeyId);
        Assert.NotNull(replacement);
        Assert.Equal(PrizeKeyStatus.Assigned, replacement.Status);
        Assert.Equal(prizeKey.Id, replacement.ReplacesPrizeKeyId);
        Assert.Equal(spin.Id, replacement.AssignedSpinId);

        // Verify claim duration matches WheelVersion
        Assert.Equal(replacement.AssignedAtUtc!.Value.AddMinutes(version.ClaimDurationMinutes), replacement.ExpiresAtUtc);
    }

    [Fact]
    public async Task CreateReplacementKeyAsync_FromExpiredKey_CreatesAssignedReplacementKey()
    {
        var (_, _, _, _, prizeKey) = await SeedAssignedKeyAsync();

        // Expire the key
        _clock.Set(prizeKey.ExpiresAtUtc!.Value.AddMinutes(1));
        prizeKey.Expire(_clock.UtcNow.UtcDateTime);
        await _db.SaveChangesAsync();

        var response = await _service.CreateReplacementKeyAsync(prizeKey.Id);

        Assert.Equal("Assigned", response.Status);
        Assert.Equal(prizeKey.Id, response.OriginalPrizeKeyId);
    }

    [Theory]
    [InlineData(PrizeKeyStatus.Available)]
    [InlineData(PrizeKeyStatus.Assigned)]
    [InlineData(PrizeKeyStatus.Redeemed)]
    public async Task CreateReplacementKeyAsync_WhenInvalidStatus_ThrowsConflictException(PrizeKeyStatus status)
    {
        var (_, _, _, _, prizeKey) = await SeedAssignedKeyAsync();

        if (status == PrizeKeyStatus.Redeemed)
        {
            await _service.RedeemKeyAsync(prizeKey.Id, new RedeemPrizeKeyRequest(null));
        }

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            _service.CreateReplacementKeyAsync(prizeKey.Id));

        Assert.Equal("PRIZE_KEY_INVALID_STATUS_FOR_REPLACEMENT", ex.Code);
    }

    [Fact]
    public async Task CreateReplacementKeyAsync_DuplicateReplacement_ThrowsConflictException()
    {
        var (_, _, _, _, prizeKey) = await SeedAssignedKeyAsync();
        await _service.CancelKeyAsync(prizeKey.Id, new CancelPrizeKeyRequest("Huỷ"));

        // First replacement
        await _service.CreateReplacementKeyAsync(prizeKey.Id);

        // Second replacement attempt
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            _service.CreateReplacementKeyAsync(prizeKey.Id));

        Assert.Equal("PRIZE_KEY_REPLACEMENT_ALREADY_EXISTS", ex.Code);
    }

    // ── 4. Spin History Query Tests ───────────────────────────────────────────

    [Fact]
    public async Task GetWheelSpinsAsync_ReturnsMaskedEmailAndNoSecrets()
    {
        var (wheel, version, prize, spin, _) = await SeedAssignedKeyAsync();

        var filter = new AdminSpinHistoryFilter(PageNumber: 1, PageSize: 20);
        var result = await _service.GetWheelSpinsAsync(wheel.Id, filter);

        Assert.Equal(1, result.TotalCount);
        var item = Assert.Single(result.Items);
        Assert.Equal(spin.Id, item.Id);
        Assert.Equal(wheel.Id, item.WheelId);
        Assert.Equal(version.Id, item.WheelVersionId);
        Assert.Equal(1, item.VersionNumber);
        Assert.Equal("n***g@gmail.com", item.MaskedEmail);
        Assert.Equal("Win", item.Result);
        Assert.Equal("Completed", item.Status);
        Assert.Equal("Voucher 500k", item.PrizeName);
        Assert.Equal("Assigned", item.PrizeKeyStatus);
    }

    [Fact]
    public async Task GetWheelSpinsAsync_WheelNotFound_ThrowsNotFoundException()
    {
        var filter = new AdminSpinHistoryFilter(PageNumber: 1, PageSize: 20);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _service.GetWheelSpinsAsync(Guid.NewGuid(), filter));
    }

    [Theory]
    [InlineData("nguyenvanquang@gmail.com")]
    [InlineData("Nguyen.Van.Quang+promo@GMAIL.COM")]
    [InlineData("  n.g.u.y.e.n.v.a.n.q.u.a.n.g@gmail.com  ")]
    public async Task GetWheelSpinsAsync_FilterByGmailVariant_MatchesNormalizedEmail(string filterEmail)
    {
        var (wheel, _, _, spin, _) = await SeedAssignedKeyAsync();

        var filter = new AdminSpinHistoryFilter(PageNumber: 1, PageSize: 20, Email: filterEmail);
        var result = await _service.GetWheelSpinsAsync(wheel.Id, filter);

        Assert.Equal(1, result.TotalCount);
        var item = Assert.Single(result.Items);
        Assert.Equal(spin.Id, item.Id);
        Assert.Equal("n***g@gmail.com", item.MaskedEmail);
    }

    [Theory]
    [InlineData("user@yahoo.com")]
    [InlineData(".nguyenvanquang@gmail.com")]
    [InlineData("not-an-email")]
    public async Task GetWheelSpinsAsync_FilterByInvalidGmail_ReturnsEmptyResult(string invalidEmail)
    {
        var (wheel, _, _, _, _) = await SeedAssignedKeyAsync();

        var filter = new AdminSpinHistoryFilter(PageNumber: 1, PageSize: 20, Email: invalidEmail);
        var result = await _service.GetWheelSpinsAsync(wheel.Id, filter);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetWheelSpinsAsync_AfterPrizeRenamed_ReturnsOriginalPrizeNameSnapshot()
    {
        var (wheel, _, prize, spin, _) = await SeedAssignedKeyAsync();

        // Admin renames the prize after the spin already occurred
        prize.Update("Voucher 300k Đã Đổi", "Mô tả mới", null, true, 10, _clock.UtcNow.UtcDateTime.AddMinutes(10));
        await _db.SaveChangesAsync();

        var result = await _service.GetWheelSpinsAsync(wheel.Id, new AdminSpinHistoryFilter(PageNumber: 1, PageSize: 20));

        var item = Assert.Single(result.Items);
        Assert.Equal(spin.Id, item.Id);
        Assert.Equal(prize.Id, item.PrizeId);
        Assert.Equal("Voucher 500k", item.PrizeName);
    }

    [Fact]
    public async Task GetWheelSpinsAsync_LegacyWinWithoutSnapshot_ReturnsNeutralFallbackWithoutUsingCurrentPrizeName()
    {
        var (wheel, _, prize, spin, _) = await SeedAssignedKeyAsync();

        // Simulate legacy Win row created before snapshot columns existed
        _db.Entry(spin).Property(x => x.PrizeNameSnapshot).CurrentValue = null;
        _db.Entry(spin).Property(x => x.PrizeDescriptionSnapshot).CurrentValue = null;
        prize.Update("Voucher 999k Hiện Tại", "Mô tả hiện tại", null, true, 10, _clock.UtcNow.UtcDateTime.AddMinutes(10));
        await _db.SaveChangesAsync();

        var result = await _service.GetWheelSpinsAsync(wheel.Id, new AdminSpinHistoryFilter(PageNumber: 1, PageSize: 20));

        var item = Assert.Single(result.Items);
        Assert.Equal(spin.Id, item.Id);
        Assert.Equal(SpinHistory.LegacyMissingPrizeNameFallback, item.PrizeName);
    }

    [Fact]
    public async Task GetWheelSpinsAsync_NoPrizeSpin_ReturnsNullPrizeName()
    {
        var (wheel, version, _, _, _) = await SeedAssignedKeyAsync();
        var now = _clock.UtcNow.UtcDateTime.AddMinutes(5);
        var noPrizeSpin = SpinHistory.CreateNoPrize(
            wheel.Id,
            version.Id,
            Guid.NewGuid(),
            "other@gmail.com",
            "other@gmail.com",
            "idemp-noprize-hist",
            "RC-TOKEN-NP",
            now,
            now);
        _db.SpinHistories.Add(noPrizeSpin);
        await _db.SaveChangesAsync();

        var result = await _service.GetWheelSpinsAsync(wheel.Id, new AdminSpinHistoryFilter(PageNumber: 1, PageSize: 20, Result: SpinResult.NoPrize));

        var item = Assert.Single(result.Items);
        Assert.Equal(noPrizeSpin.Id, item.Id);
        Assert.Null(item.PrizeId);
        Assert.Null(item.PrizeName);
    }

    // ── 5. Expiration Processor Tests ──────────────────────────────────────────

    [Fact]
    public async Task ProcessExpiredKeysBatchAsync_ExpiresOverdueAssignedKeys()
    {
        var (_, _, _, _, prizeKey) = await SeedAssignedKeyAsync();

        // Advance time past expiration
        _clock.Set(prizeKey.ExpiresAtUtc!.Value.AddMinutes(5));

        var result = await _expirationProcessor.ProcessExpiredKeysBatchAsync(100);

        Assert.Equal(1, result.ProcessedCount);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains(prizeKey.Id, result.ExpiredKeyIds);

        var updatedKey = await _db.PrizeKeys.FindAsync(prizeKey.Id);
        Assert.NotNull(updatedKey);
        Assert.Equal(PrizeKeyStatus.Expired, updatedKey.Status);
        Assert.NotNull(updatedKey.ExpiredAtUtc);
    }

    [Fact]
    public async Task ProcessExpiredKeysBatchAsync_DoesNotExpireActiveOrOtherStatusKeys()
    {
        var (_, _, _, _, prizeKey) = await SeedAssignedKeyAsync();

        // Time is BEFORE expiration
        _clock.Set(prizeKey.AssignedAtUtc!.Value.AddMinutes(10));

        var result = await _expirationProcessor.ProcessExpiredKeysBatchAsync(100);

        Assert.Equal(0, result.ProcessedCount);
        Assert.Equal(0, result.SuccessCount);

        var key = await _db.PrizeKeys.FindAsync(prizeKey.Id);
        Assert.NotNull(key);
        Assert.Equal(PrizeKeyStatus.Assigned, key.Status);
    }

    // ── 6. Background Service Execution Tests ─────────────────────────────────

    [Fact]
    public async Task BackgroundService_HandlesCancellationGracefully()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IPrizeKeyExpirationProcessor>(_expirationProcessor);
        var provider = serviceCollection.BuildServiceProvider();

        var options = Options.Create(new PrizeKeyExpirationOptions { IntervalSeconds = 1, BatchSize = 10, Enabled = true });
        var worker = new PrizeKeyExpirationBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<PrizeKeyExpirationBackgroundService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        await worker.StartAsync(cts.Token);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class MutableClock(DateTimeOffset initial) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = initial;
        public void Set(DateTimeOffset newTime) => UtcNow = newTime;
    }

    private sealed class TestAdminContext(Guid adminId) : ICurrentAdminContext
    {
        public Guid? AdminId { get; } = adminId;
        public bool IsAuthenticated => true;
    }

    private sealed class RowVersionInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            PopulateRowVersions(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
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
}
