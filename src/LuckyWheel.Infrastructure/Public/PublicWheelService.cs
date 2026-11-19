using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Application.Features.Admin.PrizeKeys;
using LuckyWheel.Application.Features.Public;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using LuckyWheel.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuckyWheel.Infrastructure.Public;

public sealed class PublicWheelService(
    ApplicationDbContext db,
    IWeightedPrizeSelector prizeSelector,
    IRandomNumberProvider randomProvider,
    IPrizeKeyProtector keyProtector,
    IClock clock,
    ILogger<PublicWheelService>? logger = null) : IPublicWheelService
{
    private readonly ILogger<PublicWheelService> _logger = logger ?? NullLogger<PublicWheelService>.Instance;
    public async Task<PublicWheelDto> GetActiveWheelBySlugAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw NotFoundException.WithCode("WHEEL_NOT_AVAILABLE", "Wheel is not available.");

        var trimmedSlug = slug.Trim();
        var now = clock.UtcNow.UtcDateTime;

        var wheel = await db.Wheels.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Slug == trimmedSlug, ct);

        if (wheel == null || !wheel.IsEnabled)
            throw NotFoundException.WithCode("WHEEL_NOT_AVAILABLE", "Wheel is not available.");

        var activeVersion = await db.WheelVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WheelId == wheel.Id && x.Status == WheelVersionStatus.Active && x.StartAtUtc <= now && x.EndAtUtc >= now, ct);

        if (activeVersion == null)
            throw NotFoundException.WithCode("WHEEL_NOT_AVAILABLE", "Wheel is not available.");

        var segments = await db.WheelVersionPrizes.AsNoTracking()
            .Where(x => x.WheelVersionId == activeVersion.Id)
            .OrderBy(x => x.DisplayOrder)
            .ToListAsync(ct);

        var prizeIds = segments.Where(x => !x.IsNoPrize && x.PrizeId.HasValue)
            .Select(x => x.PrizeId!.Value)
            .Distinct()
            .ToList();

        var prizes = await db.Prizes.AsNoTracking()
            .Where(x => prizeIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        var entries = segments.Select(s =>
        {
            string label;
            string? imageUrl = s.ImageUrl;

            if (s.IsNoPrize || !s.PrizeId.HasValue)
            {
                label = "Chúc bạn may mắn lần sau";
            }
            else if (prizes.TryGetValue(s.PrizeId.Value, out var prize))
            {
                label = prize.Name;
                imageUrl ??= prize.ImageUrl;
            }
            else
            {
                label = "Phần thưởng";
            }

            return new PublicWheelEntryDto(
                s.Id,
                s.DisplayOrder,
                label,
                s.Color,
                imageUrl,
                s.IsNoPrize);
        }).ToList();

        return new PublicWheelDto(
            wheel.Id,
            wheel.Name,
            wheel.Slug,
            activeVersion.Id,
            activeVersion.VersionNumber,
            entries);
    }

    public async Task<SpinResultDto> SpinAsync(
        string slug,
        string? idempotencyKeyHeader,
        SpinRequest request,
        CancellationToken ct = default)
    {
        PublicSpinValidation.ValidateSpinRequest(idempotencyKeyHeader, request);

        var trimmedSlug = slug.Trim();
        var trimmedIdempotencyKey = idempotencyKeyHeader!.Trim();
        var normalizedEmail = PublicSpinValidation.NormalizeEmail(request.Email!);
        var originalEmail = request.Email!.Trim();

        // 1. Replay check prior to transaction
        var existingSpin = await db.SpinHistories.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == trimmedIdempotencyKey, ct);

        if (existingSpin != null)
        {
            var targetWheel = await db.Wheels.AsNoTracking().SingleOrDefaultAsync(x => x.Slug == trimmedSlug, ct);
            if (targetWheel == null || existingSpin.WheelId != targetWheel.Id || !string.Equals(existingSpin.EmailNormalized, normalizedEmail, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "Idempotency key has already been used with different request parameters.");
            }

            return await BuildReplayResponseAsync(existingSpin, ct);
        }

        // 2. Transactional execution
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var now = clock.UtcNow.UtcDateTime;

            // Re-verify idempotency inside transaction
            var insideSpin = await db.SpinHistories
                .SingleOrDefaultAsync(x => x.IdempotencyKey == trimmedIdempotencyKey, ct);

            if (insideSpin != null)
            {
                var targetWheel = await db.Wheels.AsNoTracking().SingleOrDefaultAsync(x => x.Slug == trimmedSlug, ct);
                if (targetWheel == null || insideSpin.WheelId != targetWheel.Id || !string.Equals(insideSpin.EmailNormalized, normalizedEmail, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "Idempotency key has already been used with different request parameters.");
                }

                return await BuildReplayResponseAsync(insideSpin, ct);
            }

            // Verify Wheel is active and enabled
            var wheel = await db.Wheels
                .SingleOrDefaultAsync(x => x.Slug == trimmedSlug, ct);

            if (wheel == null || !wheel.IsEnabled)
                throw NotFoundException.WithCode("WHEEL_NOT_AVAILABLE", "Wheel is not available.");

            // Verify Active Version
            var activeVersion = await db.WheelVersions
                .SingleOrDefaultAsync(x => x.WheelId == wheel.Id && x.Status == WheelVersionStatus.Active && x.StartAtUtc <= now && x.EndAtUtc >= now, ct);

            if (activeVersion == null)
                throw NotFoundException.WithCode("WHEEL_NOT_AVAILABLE", "Wheel is not available.");

            // Verify WinnerLock
            var alreadyWon = await db.WinnerLocks
                .AnyAsync(x => x.WheelId == wheel.Id && x.EmailNormalized == normalizedEmail && x.IsActive, ct);

            if (alreadyWon)
                throw new ConflictException("EMAIL_ALREADY_WON", "This email has already won a prize on this wheel.");

            // Load active version segments
            var segments = await db.WheelVersionPrizes
                .Where(x => x.WheelVersionId == activeVersion.Id)
                .OrderBy(x => x.DisplayOrder)
                .ToListAsync(ct);

            var noPrizeSegment = ValidateAndGetNoPrizeSegment(activeVersion.Id, segments);

            var prizeIds = segments
                .Where(x => !x.IsNoPrize)
                .Select(x => x.PrizeId!.Value)
                .Distinct()
                .ToList();

            var prizes = prizeIds.Count == 0
                ? new Dictionary<Guid, Prize>()
                : await db.Prizes
                    .Where(x => prizeIds.Contains(x.Id))
                    .ToDictionaryAsync(x => x.Id, ct);

            ValidateReferencedPrizes(wheel.Id, activeVersion.Id, prizeIds, prizes);

            // Weighted selection across all configured segments on the 1_000_000 scale
            var selectedSegment = prizeSelector.Select(segments);
            var receiptToken = GenerateReceiptToken();

            if (!selectedSegment.IsNoPrize)
            {
                var prize = prizes[selectedSegment.PrizeId!.Value];
                PrizeKey? allocatedKey = null;
                string? plaintextKey = null;
                DateTime? keyExpiresAtUtc = null;
                var canAwardWin = true;

                if (prize.RequiresKey)
                {
                    allocatedKey = await TryAllocateAvailableKeyAsync(prize.Id, ct);
                    if (allocatedKey == null)
                    {
                        canAwardWin = false;
                        _logger.LogInformation(
                            "Spin fell back to NoPrize because no available prize key could be allocated. WheelId={WheelId} WheelVersionId={WheelVersionId} SelectedSegmentId={SelectedSegmentId} PrizeId={PrizeId} FallbackSegmentId={FallbackSegmentId} Reason={Reason}",
                            wheel.Id,
                            activeVersion.Id,
                            selectedSegment.Id,
                            prize.Id,
                            noPrizeSegment.Id,
                            "NO_AVAILABLE_PRIZE_KEY");
                    }
                    else
                    {
                        keyExpiresAtUtc = now.AddMinutes(activeVersion.ClaimDurationMinutes);
                        plaintextKey = keyProtector.Unprotect(allocatedKey.EncryptedCode, allocatedKey.EncryptionNonce, allocatedKey.EncryptionTag);
                    }
                }

                if (canAwardWin)
                {
                    var winSpin = SpinHistory.CreateWin(
                        wheel.Id,
                        activeVersion.Id,
                        selectedSegment.Id,
                        originalEmail,
                        normalizedEmail,
                        prize.Id,
                        allocatedKey?.Id,
                        trimmedIdempotencyKey,
                        receiptToken,
                        now,
                        now);

                    if (allocatedKey != null && keyExpiresAtUtc.HasValue)
                    {
                        allocatedKey.Assign(winSpin.Id, now, keyExpiresAtUtc.Value);
                    }

                    var winnerLock = new WinnerLock(
                        wheel.Id,
                        normalizedEmail,
                        winSpin.Id,
                        allocatedKey?.Id,
                        now);

                    db.SpinHistories.Add(winSpin);
                    db.WinnerLocks.Add(winnerLock);

                    await SaveAndCommitAsync(tx, ct);

                    return new SpinResultDto(
                        winSpin.Id,
                        winSpin.ReceiptToken,
                        "Win",
                        selectedSegment.Id,
                        selectedSegment.DisplayOrder,
                        "Chúc mừng bạn đã trúng thưởng",
                        new SpinPrizeDto(
                            prize.Id,
                            prize.Name,
                            prize.Description,
                            plaintextKey,
                            keyExpiresAtUtc),
                        winSpin.CreatedAtUtc);
                }
            }

            // Shared NoPrize path for both direct NoPrize draws and out-of-key fallbacks
            var noPrizeSpin = SpinHistory.CreateNoPrize(
                wheel.Id,
                activeVersion.Id,
                noPrizeSegment.Id,
                originalEmail,
                normalizedEmail,
                trimmedIdempotencyKey,
                receiptToken,
                now,
                now);

            db.SpinHistories.Add(noPrizeSpin);
            await SaveAndCommitAsync(tx, ct);

            return new SpinResultDto(
                noPrizeSpin.Id,
                noPrizeSpin.ReceiptToken,
                "NoPrize",
                noPrizeSegment.Id,
                noPrizeSegment.DisplayOrder,
                "Chúc bạn may mắn lần sau",
                null,
                noPrizeSpin.CreatedAtUtc);
        });
    }

    private static WheelVersionPrize ValidateAndGetNoPrizeSegment(
        Guid wheelVersionId,
        IReadOnlyList<WheelVersionPrize> segments)
    {
        if (segments.Count == 0)
        {
            throw new InvalidOperationException(
                $"Active wheel version {wheelVersionId} has no configured segments.");
        }

        long totalWeight = 0;
        WheelVersionPrize? noPrizeSegment = null;
        var noPrizeCount = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.ProbabilityWeight <= 0)
            {
                throw new InvalidOperationException(
                    $"Active wheel version {wheelVersionId} segment {segment.Id} has non-positive weight {segment.ProbabilityWeight}.");
            }

            totalWeight += segment.ProbabilityWeight;

            if (segment.IsNoPrize)
            {
                if (segment.PrizeId.HasValue)
                {
                    throw new InvalidOperationException(
                        $"Active wheel version {wheelVersionId} NoPrize segment {segment.Id} must not reference a prize.");
                }

                noPrizeSegment = segment;
                noPrizeCount++;
            }
            else if (!segment.PrizeId.HasValue)
            {
                throw new InvalidOperationException(
                    $"Active wheel version {wheelVersionId} prize segment {segment.Id} is missing PrizeId.");
            }
        }

        if (totalWeight != WeightedPrizeSelector.ExpectedTotalWeight)
        {
            throw new InvalidOperationException(
                $"Active wheel version {wheelVersionId} total probability weight must equal {WeightedPrizeSelector.ExpectedTotalWeight}, but was {totalWeight}.");
        }

        if (noPrizeCount != 1 || noPrizeSegment == null)
        {
            throw new InvalidOperationException(
                $"Active wheel version {wheelVersionId} must contain exactly one NoPrize segment, but found {noPrizeCount}.");
        }

        return noPrizeSegment;
    }

    private static void ValidateReferencedPrizes(
        Guid wheelId,
        Guid wheelVersionId,
        IReadOnlyList<Guid> prizeIds,
        IReadOnlyDictionary<Guid, Prize> prizes)
    {
        for (var i = 0; i < prizeIds.Count; i++)
        {
            var prizeId = prizeIds[i];
            if (!prizes.TryGetValue(prizeId, out var prize) || !prize.IsEnabled || prize.WheelId != wheelId)
            {
                throw new InvalidOperationException(
                    $"Active wheel version {wheelVersionId} references an invalid, disabled, or cross-wheel prize {prizeId}.");
            }
        }
    }

    private async Task<PrizeKey?> TryAllocateAvailableKeyAsync(Guid prizeId, CancellationToken ct)
    {
        // Atomic key allocation with SQL Server locking hints (UPDLOCK, READPAST, ROWLOCK)
        if (db.Database.IsSqlServer())
        {
            return await db.PrizeKeys
                .FromSqlRaw(
                    "SELECT TOP (1) * FROM [PrizeKeys] WITH (UPDLOCK, READPAST, ROWLOCK) WHERE [PrizeId] = {0} AND [Status] = 'Available' ORDER BY [CreatedAtUtc] ASC",
                    prizeId)
                .SingleOrDefaultAsync(ct);
        }

        return await db.PrizeKeys
            .Where(k => k.PrizeId == prizeId && k.Status == PrizeKeyStatus.Available)
            .OrderBy(k => k.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    private string GenerateReceiptToken()
    {
        var randomBytes = randomProvider.GetBytes(16);
        return "RC-" + Convert.ToHexString(randomBytes);
    }

    private async Task<SpinResultDto> BuildReplayResponseAsync(SpinHistory spin, CancellationToken ct)
    {
        var segment = await db.WheelVersionPrizes.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == spin.WheelVersionPrizeId, ct);

        int displayOrder = segment?.DisplayOrder ?? 1;

        if (spin.Result == SpinResult.NoPrize)
        {
            return new SpinResultDto(
                spin.Id,
                spin.ReceiptToken,
                "NoPrize",
                spin.WheelVersionPrizeId,
                displayOrder,
                "Chúc bạn may mắn lần sau",
                null,
                spin.CreatedAtUtc);
        }

        SpinPrizeDto? prizeDto = null;
        if (spin.PrizeId.HasValue)
        {
            var prize = await db.Prizes.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == spin.PrizeId.Value, ct);

            string? plaintextKey = null;
            DateTime? keyExpiresAtUtc = null;

            if (spin.PrizeKeyId.HasValue)
            {
                var key = await db.PrizeKeys.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == spin.PrizeKeyId.Value, ct);

                if (key != null)
                {
                    plaintextKey = keyProtector.Unprotect(key.EncryptedCode, key.EncryptionNonce, key.EncryptionTag);
                    keyExpiresAtUtc = key.ExpiresAtUtc;
                }
            }

            if (prize != null)
            {
                prizeDto = new SpinPrizeDto(
                    prize.Id,
                    prize.Name,
                    prize.Description,
                    plaintextKey,
                    keyExpiresAtUtc);
            }
        }

        return new SpinResultDto(
            spin.Id,
            spin.ReceiptToken,
            "Win",
            spin.WheelVersionPrizeId,
            displayOrder,
            "Chúc mừng bạn đã trúng thưởng",
            prizeDto,
            spin.CreatedAtUtc);
    }

    private async Task SaveAndCommitAsync(IDbContextTransaction tx, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException sqlEx && (sqlEx.Number == 2601 || sqlEx.Number == 2627))
        {
            if (sqlEx.Message.Contains("IX_WinnerLocks_WheelId_EmailNormalized", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("WinnerLocks", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConflictException("EMAIL_ALREADY_WON", "This email has already won a prize on this wheel.");
            }

            if (sqlEx.Message.Contains("IX_SpinHistories_IdempotencyKey", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "Idempotency key has already been used.");
            }

            throw new ConflictException("CONFLICT", "A database conflict occurred while saving spin results. Please retry.");
        }
    }
}
