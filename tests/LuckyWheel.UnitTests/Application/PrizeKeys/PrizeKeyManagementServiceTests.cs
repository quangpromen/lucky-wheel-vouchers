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
using Microsoft.Extensions.Options;
using Xunit;

namespace LuckyWheel.UnitTests.Application.PrizeKeys;

public sealed class PrizeKeyManagementServiceTests
{
    private readonly ApplicationDbContext _db;
    private readonly IPrizeKeyGenerator _generator;
    private readonly IPrizeKeyProtector _protector;
    private readonly TestClock _clock;
    private readonly TestAdminContext _adminContext;
    private readonly PrizeKeyService _service;

    public PrizeKeyManagementServiceTests()
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
        _clock = new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        _adminContext = new TestAdminContext(Guid.NewGuid());

        _service = new PrizeKeyService(_db, _generator, _protector, _clock, _adminContext);
    }

    [Fact]
    public async Task GenerateKeys_PrizeRequiresKey_GeneratesAvailableKeysWithoutSecretsInResponse()
    {
        var wheel = new Wheel("Campaign", "campaign", null, "Terms", _clock.UtcNow.UtcDateTime);
        _db.Wheels.Add(wheel);
        var prize = new Prize(wheel.Id, "Voucher 100k", null, null, requiresKey: true, totalQuantity: 100, _clock.UtcNow.UtcDateTime);
        _db.Prizes.Add(prize);
        await _db.SaveChangesAsync();

        var response = await _service.GenerateKeysAsync(prize.Id, new GeneratePrizeKeysRequest(10));

        Assert.Equal(prize.Id, response.PrizeId);
        Assert.Equal(10, response.GeneratedCount);
        Assert.Equal("Available", response.Status);
        Assert.Equal(_clock.UtcNow.UtcDateTime, response.CreatedAtUtc);

        var keys = await _db.PrizeKeys.Where(x => x.PrizeId == prize.Id).ToListAsync();
        Assert.Equal(10, keys.Count);
        Assert.All(keys, k =>
        {
            Assert.Equal(PrizeKeyStatus.Available, k.Status);
            Assert.NotNull(k.CodeHash);
            Assert.Equal(64, k.CodeHash.Length);
            Assert.NotNull(k.EncryptedCode);
            Assert.NotNull(k.EncryptionNonce);
            Assert.NotNull(k.EncryptionTag);
            Assert.Null(k.AssignedSpinId);
            Assert.Null(k.AssignedAtUtc);
            Assert.Null(k.ExpiresAtUtc);
            Assert.Null(k.RedeemedAtUtc);
            Assert.Null(k.CancelledAtUtc);
        });

        // Audit log verified
        var audit = await _db.AuditLogs.SingleOrDefaultAsync(x => x.EntityType == "PrizeKey" && x.EntityId == prize.Id);
        Assert.NotNull(audit);
        Assert.Equal(AuditAction.Created, audit.Action);
        Assert.Equal(_adminContext.AdminId, audit.AdminUserId);
    }

    [Fact]
    public async Task GenerateKeys_PrizeDoesNotRequireKey_ThrowsBusinessRuleViolation()
    {
        var wheel = new Wheel("Campaign", "campaign-no-key", null, "Terms", _clock.UtcNow.UtcDateTime);
        _db.Wheels.Add(wheel);
        var prize = new Prize(wheel.Id, "Thank You", null, null, requiresKey: false, totalQuantity: 0, _clock.UtcNow.UtcDateTime);
        _db.Prizes.Add(prize);
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessRuleViolationException>(
            () => _service.GenerateKeysAsync(prize.Id, new GeneratePrizeKeysRequest(5)));

        Assert.Equal("PRIZE_DOES_NOT_REQUIRE_KEY", ex.RuleCode);
    }

    [Fact]
    public async Task GenerateKeys_PrizeNotFound_ThrowsNotFoundException()
    {
        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => _service.GenerateKeysAsync(Guid.NewGuid(), new GeneratePrizeKeysRequest(5)));

        Assert.Equal("NOT_FOUND", NotFoundException.ErrorCode);
        Assert.Contains("Prize", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task GenerateKeys_InvalidQuantity_ThrowsValidationException(int quantity)
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => _service.GenerateKeysAsync(Guid.NewGuid(), new GeneratePrizeKeysRequest(quantity)));

        Assert.Equal("VALIDATION_ERROR", ValidationException.ErrorCode);
        Assert.True(ex.Errors.ContainsKey("quantity"));
    }

    [Fact]
    public async Task GetKeys_PaginationAndFiltering_WorksAccurately()
    {
        var wheel = new Wheel("Campaign", "campaign-filter", null, "Terms", _clock.UtcNow.UtcDateTime);
        _db.Wheels.Add(wheel);
        var prize1 = new Prize(wheel.Id, "Prize 1", null, null, true, 50, _clock.UtcNow.UtcDateTime);
        var prize2 = new Prize(wheel.Id, "Prize 2", null, null, true, 50, _clock.UtcNow.UtcDateTime);
        _db.Prizes.AddRange(prize1, prize2);

        for (int i = 0; i < 5; i++)
        {
            var key1 = TestPrizeKey(prize1.Id, $"LW-P1A0-TEST-000{i}-ABCD", i);
            var key2 = TestPrizeKey(prize2.Id, $"LW-P2B0-TEST-000{i}-EFGH", i);
            _db.PrizeKeys.AddRange(key1, key2);
        }
        await _db.SaveChangesAsync();

        // Filter by prize1
        var resultP1 = await _service.GetKeysAsync(pageNumber: 1, pageSize: 10, prizeId: prize1.Id, status: null);
        Assert.Equal(5, resultP1.TotalCount);
        Assert.Equal(5, resultP1.Items.Count);
        Assert.All(resultP1.Items, item =>
        {
            Assert.Equal(prize1.Id, item.PrizeId);
            Assert.Equal("Prize 1", item.PrizeName);
            Assert.StartsWith("LW-P1A0-TEST-", item.Code);
            Assert.Equal("Available", item.Status);
        });

        // Filter by Available
        var resultAvailable = await _service.GetKeysAsync(pageNumber: 1, pageSize: 20, prizeId: null, status: PrizeKeyStatus.Available);
        Assert.Equal(10, resultAvailable.TotalCount);

        // Filter by specific Code
        var resultCode = await _service.GetKeysAsync(pageNumber: 1, pageSize: 10, prizeId: null, status: null, code: "LW-P1A0-TEST-0002-ABCD");
        Assert.Equal(1, resultCode.TotalCount);
        var found = Assert.Single(resultCode.Items);
        Assert.Equal("LW-P1A0-TEST-0002-ABCD", found.Code);
        Assert.Equal(prize1.Id, found.PrizeId);
    }

    [Fact]
    public async Task GetKeyById_ExistingKey_ReturnsDecryptedCodeAndMetadata()
    {
        var wheel = new Wheel("Campaign", "campaign-getbyid", null, "Terms", _clock.UtcNow.UtcDateTime);
        _db.Wheels.Add(wheel);
        var prize = new Prize(wheel.Id, "Special Voucher", null, null, true, 10, _clock.UtcNow.UtcDateTime);
        _db.Prizes.Add(prize);
        var key = TestPrizeKey(prize.Id, "LW-VOUC-HER1-2345-6789", 0);
        _db.PrizeKeys.Add(key);
        await _db.SaveChangesAsync();

        var dto = await _service.GetKeyByIdAsync(key.Id);

        Assert.Equal(key.Id, dto.Id);
        Assert.Equal(prize.Id, dto.PrizeId);
        Assert.Equal("Special Voucher", dto.PrizeName);
        Assert.Equal("LW-VOUC-HER1-2345-6789", dto.Code);
        Assert.Equal("Available", dto.Status);
        Assert.Equal(_clock.UtcNow.UtcDateTime, dto.CreatedAtUtc);
        Assert.Null(dto.AssignedAtUtc);
    }

    private PrizeKey TestPrizeKey(Guid prizeId, string plaintextKey, int minute)
    {
        var protectedKey = _protector.Protect(plaintextKey);
        return new(prizeId, protectedKey.CodeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, _clock.UtcNow.UtcDateTime.AddMinutes(minute));
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
