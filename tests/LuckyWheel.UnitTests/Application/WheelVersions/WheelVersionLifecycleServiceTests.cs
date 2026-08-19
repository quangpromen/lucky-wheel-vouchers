using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Application.Common.Authentication;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Application.Common.Validation;
using LuckyWheel.Application.Features.Admin;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using LuckyWheel.Infrastructure.Admin;
using LuckyWheel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LuckyWheel.UnitTests.Application.WheelVersions;

public sealed class WheelVersionLifecycleServiceTests
{
    private readonly ApplicationDbContext _db;
    private readonly TestClock _clock;
    private readonly TestAdminContext _adminContext;
    private readonly AdminManagementService _service;

    public WheelVersionLifecycleServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new RowVersionInterceptor())
            .Options;

        _db = new ApplicationDbContext(options);
        _clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        _adminContext = new TestAdminContext(Guid.NewGuid());
        _service = new AdminManagementService(_db, _clock, _adminContext);
    }

    [Fact]
    public async Task ActivateVersion_ValidDraftWithAvailableKeys_SuccessfullyActivates()
    {
        var (wheel, version, prize) = await SeedDraftVersionAsync(requiresKey: true);
        var key = TestPrizeKey(prize.Id);
        _db.PrizeKeys.Add(key);
        await _db.SaveChangesAsync();

        var validRowVersion = GetRowVersion(version);
        var result = await _service.ActivateVersionAsync(version.Id, new ActivateDraftWheelVersionRequest(validRowVersion), default);

        Assert.Equal("Active", result.Status);
        Assert.Equal(version.Id, result.Id);

        var updated = await _db.WheelVersions.FindAsync(version.Id);
        Assert.NotNull(updated);
        Assert.Equal(WheelVersionStatus.Active, updated.Status);
        Assert.Equal(_adminContext.AdminId, updated.PublishedByAdminId);
        Assert.Equal(_clock.UtcNow.UtcDateTime, updated.PublishedAtUtc);

        var audit = await _db.AuditLogs.SingleOrDefaultAsync(x => x.EntityType == "WheelVersion" && x.EntityId == version.Id && x.Action == AuditAction.Activated);
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task ActivateVersion_StaleRowVersion_ThrowsConflictException()
    {
        var (wheel, version, prize) = await SeedDraftVersionAsync(requiresKey: true);
        var key = TestPrizeKey(prize.Id);
        _db.PrizeKeys.Add(key);
        await _db.SaveChangesAsync();

        var staleRowVersion = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => _service.ActivateVersionAsync(version.Id, new ActivateDraftWheelVersionRequest(staleRowVersion), default));

        Assert.Equal("CONFLICT", ConflictException.ErrorCode);
    }

    [Fact]
    public async Task ActivateVersion_NoSegments_ThrowsBusinessRuleViolation()
    {
        var wheel = new Wheel("Wheel", "wheel-empty-segments", null, "Terms", _clock.UtcNow.UtcDateTime);
        _db.Wheels.Add(wheel);
        var version = new WheelVersion(wheel.Id, 1, _clock.UtcNow.UtcDateTime, _clock.UtcNow.UtcDateTime.AddDays(7), 60, _clock.UtcNow.UtcDateTime);
        _db.WheelVersions.Add(version);
        await _db.SaveChangesAsync();

        var validRowVersion = GetRowVersion(version);
        var ex = await Assert.ThrowsAsync<BusinessRuleViolationException>(
            () => _service.ActivateVersionAsync(version.Id, new ActivateDraftWheelVersionRequest(validRowVersion), default));

        Assert.Equal("WHEEL_VERSION_NO_SEGMENTS", ex.RuleCode);
    }

    [Fact]
    public async Task ActivateVersion_KeyedPrizeWithoutAvailableKeys_ThrowsBusinessRuleViolation()
    {
        var (wheel, version, prize) = await SeedDraftVersionAsync(requiresKey: true);

        var validRowVersion = GetRowVersion(version);
        var ex = await Assert.ThrowsAsync<BusinessRuleViolationException>(
            () => _service.ActivateVersionAsync(version.Id, new ActivateDraftWheelVersionRequest(validRowVersion), default));

        Assert.Equal("PRIZE_KEY_NOT_AVAILABLE", ex.RuleCode);
    }

    [Fact]
    public async Task ActivateVersion_AnotherVersionAlreadyActive_ThrowsConflictException()
    {
        var (wheel, version, prize) = await SeedDraftVersionAsync(requiresKey: true);
        var key = TestPrizeKey(prize.Id);
        _db.PrizeKeys.Add(key);

        // Another version is already active
        var activeVersion = new WheelVersion(wheel.Id, 2, _clock.UtcNow.UtcDateTime, _clock.UtcNow.UtcDateTime.AddDays(7), 60, _clock.UtcNow.UtcDateTime);
        activeVersion.Activate(_adminContext.AdminId!.Value, _clock.UtcNow.UtcDateTime);
        _db.WheelVersions.Add(activeVersion);
        await _db.SaveChangesAsync();

        var validRowVersion = GetRowVersion(version);
        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => _service.ActivateVersionAsync(version.Id, new ActivateDraftWheelVersionRequest(validRowVersion), default));

        Assert.Equal("CONFLICT", ConflictException.ErrorCode);
        Assert.Contains("active", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CloseVersion_ActiveVersion_SuccessfullyCloses()
    {
        var (wheel, version, prize) = await SeedDraftVersionAsync(requiresKey: false);
        version.Activate(_adminContext.AdminId!.Value, _clock.UtcNow.UtcDateTime);
        await _db.SaveChangesAsync();

        var validRowVersion = GetRowVersion(version);
        var result = await _service.CloseVersionAsync(version.Id, new CloseActiveWheelVersionRequest(validRowVersion), default);

        Assert.Equal("Closed", result.Status);

        var updated = await _db.WheelVersions.FindAsync(version.Id);
        Assert.NotNull(updated);
        Assert.Equal(WheelVersionStatus.Closed, updated.Status);
        Assert.Equal(_clock.UtcNow.UtcDateTime, updated.ClosedAtUtc);

        var audit = await _db.AuditLogs.SingleOrDefaultAsync(x => x.EntityType == "WheelVersion" && x.EntityId == version.Id && x.Action == AuditAction.Closed);
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task CloseVersion_StaleRowVersion_ThrowsConflictException()
    {
        var (wheel, version, prize) = await SeedDraftVersionAsync(requiresKey: false);
        version.Activate(_adminContext.AdminId!.Value, _clock.UtcNow.UtcDateTime);
        await _db.SaveChangesAsync();

        var staleRowVersion = Convert.ToBase64String(new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 });
        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => _service.CloseVersionAsync(version.Id, new CloseActiveWheelVersionRequest(staleRowVersion), default));

        Assert.Equal("CONFLICT", ConflictException.ErrorCode);
    }

    [Fact]
    public async Task CloseVersion_DraftVersion_ThrowsConflictException()
    {
        var (wheel, version, prize) = await SeedDraftVersionAsync(requiresKey: false);

        var validRowVersion = GetRowVersion(version);
        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => _service.CloseVersionAsync(version.Id, new CloseActiveWheelVersionRequest(validRowVersion), default));

        Assert.Equal("CONFLICT", ConflictException.ErrorCode);
    }

    [Theory]
    [InlineData("invalid-base64")]
    [InlineData("AQIDBAU=")] // 5 bytes instead of 8
    public async Task ActivateVersion_InvalidRowVersion_ThrowsValidationException(string invalidRowVersion)
    {
        var (wheel, version, prize) = await SeedDraftVersionAsync(requiresKey: false);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => _service.ActivateVersionAsync(version.Id, new ActivateDraftWheelVersionRequest(invalidRowVersion), default));

        Assert.Equal("VALIDATION_ERROR", ValidationException.ErrorCode);
        Assert.True(ex.Errors.ContainsKey("rowVersion"));
    }

    private string GetRowVersion<TEntity>(TEntity entity) where TEntity : class
    {
        var bytes = _db.Entry(entity).Property<byte[]>("RowVersion").CurrentValue;
        return Convert.ToBase64String(bytes ?? new byte[8]);
    }

    private PrizeKey TestPrizeKey(Guid prizeId) => new(prizeId, "hash1", [1], new byte[12], new byte[16], _clock.UtcNow.UtcDateTime);

    private async Task<(Wheel wheel, WheelVersion version, Prize prize)> SeedDraftVersionAsync(bool requiresKey)
    {
        var wheel = new Wheel("Summer Wheel", $"summer-{Guid.NewGuid():N}", null, "Terms", _clock.UtcNow.UtcDateTime);
        _db.Wheels.Add(wheel);

        var version = new WheelVersion(wheel.Id, 1, _clock.UtcNow.UtcDateTime, _clock.UtcNow.UtcDateTime.AddDays(7), 60, _clock.UtcNow.UtcDateTime);
        _db.WheelVersions.Add(version);

        var prize = new Prize(wheel.Id, "Discount 50k", null, null, requiresKey, requiresKey ? 50 : 0, _clock.UtcNow.UtcDateTime);
        _db.Prizes.Add(prize);

        var segment1 = new WheelVersionPrize(version.Id, prize.Id, 500_000, 1, "#FF0000", null, false, _clock.UtcNow.UtcDateTime);
        var segment2 = new WheelVersionPrize(version.Id, null, 500_000, 2, "#00FF00", null, true, _clock.UtcNow.UtcDateTime);
        _db.WheelVersionPrizes.AddRange(segment1, segment2);

        await _db.SaveChangesAsync();
        return (wheel, version, prize);
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

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class TestAdminContext(Guid adminId) : ICurrentAdminContext
    {
        public Guid? AdminId { get; } = adminId;
        public bool IsAuthenticated => true;
    }
}
