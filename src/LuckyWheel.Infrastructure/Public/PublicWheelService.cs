using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
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
    private const string IdempotencyKeyIndexName = "IX_SpinHistories_IdempotencyKey";
    private const string WinnerLockIndexName = "IX_WinnerLocks_WheelId_EmailNormalized";
    private const string SpinPrizeKeyIndexName = "IX_SpinHistories_PrizeKeyId";
    private const string ReceiptTokenIndexName = "IX_SpinHistories_ReceiptToken";

    private enum SpinUniqueConstraintConflict
    {
        None = 0,
        IdempotencyKey = 1,
        WinnerLock = 2,
        PrizeKeyAssignment = 3,
        ReceiptToken = 4
    }

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
            wheel.Terms,
            activeVersion.Id,
            activeVersion.VersionNumber,
            activeVersion.StartAtUtc,
            activeVersion.EndAtUtc,
            entries);
    }

    public async Task<SpinResultDto> SpinAsync(
        string slug,
        string? idempotencyKeyHeader,
        SpinRequest request,
        CancellationToken ct = default)
    {
        PublicSpinValidation.ValidateSpinRequest(idempotencyKeyHeader, request);

        var trimmedSlug = slug?.Trim() ?? string.Empty;
        var trimmedIdempotencyKey = idempotencyKeyHeader!.Trim();
        var normalizedEmail = PublicSpinValidation.NormalizeEmail(request.Email!);
        var originalEmail = request.Email!.Trim();
        var acceptedTermsAtUtc = clock.UtcNow.UtcDateTime;

        // Transactional execution with ExecutionStrategy retry + post-rollback conflict resolution
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();

            // Fast-path idempotency check at the start of every strategy attempt (covers replays
            // even after wheel disable/version close/deadline/winner lock, concurrent completions,
            // and retries after commit-confirmation drops).
            var replay = await TryResolveIdempotencyAsync(
                trimmedSlug,
                trimmedIdempotencyKey,
                normalizedEmail,
                ct);

            if (replay != null)
            {
                return replay;
            }

            return await ExecuteSpinAttemptAsync(
                trimmedSlug,
                trimmedIdempotencyKey,
                originalEmail,
                normalizedEmail,
                acceptedTermsAtUtc,
                ct);
        });
    }

    private async Task<SpinResultDto> ExecuteSpinAttemptAsync(
        string trimmedSlug,
        string trimmedIdempotencyKey,
        string originalEmail,
        string normalizedEmail,
        DateTime acceptedTermsAtUtc,
        CancellationToken ct)
    {
        PrizeKey? allocatedKey = null;
        SpinHistory? pendingSpin = null;
        WinnerLock? pendingWinnerLock = null;

        var uniqueConflict = SpinUniqueConstraintConflict.None;
        var wheelUnavailable = false;
        var alreadyWonConflict = false;

        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            try
            {
                // Acquire transaction-scoped shared row locks on Wheel and its Active WheelVersion
                // so public spins do not block each other while serializing against Disable / Close / Activate.
                var wheel = await LoadWheelForSpinAsync(trimmedSlug, ct);
                if (wheel == null || !wheel.IsEnabled)
                {
                    await RollbackTransactionAsync(tx);
                    wheelUnavailable = true;
                }
                else
                {
                    var activeVersion = await LoadActiveVersionForSpinAsync(wheel.Id, ct);

                    // Admission point: evaluate schedule validity using a fresh UTC timestamp
                    // strictly AFTER both Wheel and Active WheelVersion locks have been acquired.
                    var admittedAtUtc = clock.UtcNow.UtcDateTime;
                    if (activeVersion == null
                        || admittedAtUtc < activeVersion.StartAtUtc
                        || admittedAtUtc > activeVersion.EndAtUtc)
                    {
                        await RollbackTransactionAsync(tx);
                        wheelUnavailable = true;
                    }
                    else
                    {
                        var alreadyWon = await db.WinnerLocks.AsNoTracking()
                            .AnyAsync(x => x.WheelId == wheel.Id && x.EmailNormalized == normalizedEmail && x.IsActive, ct);

                        if (alreadyWon)
                        {
                            await RollbackTransactionAsync(tx);
                            alreadyWonConflict = true;
                        }
                        else
                        {
                            var segments = await db.WheelVersionPrizes.AsNoTracking()
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
                                : await db.Prizes.AsNoTracking()
                                    .Where(x => prizeIds.Contains(x.Id))
                                    .ToDictionaryAsync(x => x.Id, ct);

                            ValidateReferencedPrizes(wheel.Id, activeVersion.Id, prizeIds, prizes);

                            // Weighted selection across all configured segments on the 1_000_000 scale
                            var selectedSegment = prizeSelector.Select(segments);
                            var receiptToken = GenerateReceiptToken();

                            if (!selectedSegment.IsNoPrize)
                            {
                                var prize = prizes[selectedSegment.PrizeId!.Value];
                                string? plaintextKey = null;
                                DateTime? assignedAtUtc = null;
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
                                        plaintextKey = keyProtector.Unprotect(allocatedKey.EncryptedCode, allocatedKey.EncryptionNonce, allocatedKey.EncryptionTag);
                                        assignedAtUtc = clock.UtcNow.UtcDateTime;
                                        keyExpiresAtUtc = assignedAtUtc.Value.AddMinutes(activeVersion.ClaimDurationMinutes);
                                    }
                                }

                                if (canAwardWin)
                                {
                                    var winRecordedAtUtc = assignedAtUtc ?? clock.UtcNow.UtcDateTime;

                                    pendingSpin = SpinHistory.CreateWin(
                                        wheel.Id,
                                        activeVersion.Id,
                                        selectedSegment.Id,
                                        originalEmail,
                                        normalizedEmail,
                                        prize.Id,
                                        prize.Name,
                                        prize.Description,
                                        allocatedKey?.Id,
                                        trimmedIdempotencyKey,
                                        receiptToken,
                                        acceptedTermsAtUtc,
                                        winRecordedAtUtc);

                                    if (allocatedKey != null && assignedAtUtc.HasValue && keyExpiresAtUtc.HasValue)
                                    {
                                        allocatedKey.Assign(pendingSpin.Id, assignedAtUtc.Value, keyExpiresAtUtc.Value);
                                    }

                                    pendingWinnerLock = new WinnerLock(
                                        wheel.Id,
                                        normalizedEmail,
                                        pendingSpin.Id,
                                        allocatedKey?.Id,
                                        winRecordedAtUtc);

                                    db.SpinHistories.Add(pendingSpin);
                                    db.WinnerLocks.Add(pendingWinnerLock);

                                    await SaveAndCommitAsync(tx, ct);

                                    return new SpinResultDto(
                                        pendingSpin.Id,
                                        pendingSpin.ReceiptToken,
                                        "Win",
                                        selectedSegment.Id,
                                        selectedSegment.DisplayOrder,
                                        "Chúc mừng bạn đã trúng thưởng",
                                        new SpinPrizeDto(
                                            prize.Id,
                                            pendingSpin.PrizeNameSnapshot!,
                                            pendingSpin.PrizeDescriptionSnapshot,
                                            plaintextKey,
                                            keyExpiresAtUtc),
                                        pendingSpin.CreatedAtUtc);
                                }
                            }

                            // Shared NoPrize path for both direct NoPrize draws and out-of-key fallbacks
                            var noPrizeRecordedAtUtc = clock.UtcNow.UtcDateTime;
                            pendingSpin = SpinHistory.CreateNoPrize(
                                wheel.Id,
                                activeVersion.Id,
                                noPrizeSegment.Id,
                                originalEmail,
                                normalizedEmail,
                                trimmedIdempotencyKey,
                                receiptToken,
                                acceptedTermsAtUtc,
                                noPrizeRecordedAtUtc);

                            db.SpinHistories.Add(pendingSpin);
                            await SaveAndCommitAsync(tx, ct);

                            return new SpinResultDto(
                                pendingSpin.Id,
                                pendingSpin.ReceiptToken,
                                "NoPrize",
                                noPrizeSegment.Id,
                                noPrizeSegment.DisplayOrder,
                                "Chúc bạn may mắn lần sau",
                                null,
                                pendingSpin.CreatedAtUtc);
                        }
                    }
                }
            }
            catch (DbUpdateException ex)
            {
                await RollbackTransactionAsync(tx);
                CleanupAttemptState(allocatedKey, pendingSpin, pendingWinnerLock);

                uniqueConflict = ClassifyUniqueConstraintViolation(ex);
                if (uniqueConflict == SpinUniqueConstraintConflict.None)
                {
                    throw;
                }
            }
            catch
            {
                await RollbackTransactionAsync(tx);
                CleanupAttemptState(allocatedKey, pendingSpin, pendingWinnerLock);
                throw;
            }
        }

        // Transaction is now rolled back and disposed, and attempt-local ChangeTracker state is clean.
        ct.ThrowIfCancellationRequested();

        // Always prioritize idempotency resolution over WinnerLock or Wheel/Version availability,
        // including when a concurrent WinnerLock commit raced ahead of the IdempotencyKey check/constraint.
        var postConflictReplay = await TryResolveIdempotencyAsync(
            trimmedSlug,
            trimmedIdempotencyKey,
            normalizedEmail,
            ct);

        if (postConflictReplay != null)
        {
            return postConflictReplay;
        }

        if (wheelUnavailable)
        {
            throw NotFoundException.WithCode("WHEEL_NOT_AVAILABLE", "Wheel is not available.");
        }

        if (alreadyWonConflict || uniqueConflict == SpinUniqueConstraintConflict.WinnerLock)
        {
            throw new ConflictException("EMAIL_ALREADY_WON", "This email has already won a prize on this wheel.");
        }

        if (uniqueConflict == SpinUniqueConstraintConflict.IdempotencyKey)
        {
            throw new ConflictException("IDEMPOTENCY_KEY_REUSED", "Idempotency key has already been used.");
        }

        if (uniqueConflict == SpinUniqueConstraintConflict.PrizeKeyAssignment)
        {
            throw new ConflictException("CONFLICT", "Prize key assignment conflicted with another request. Please retry.");
        }

        if (uniqueConflict == SpinUniqueConstraintConflict.ReceiptToken)
        {
            throw new ConflictException("CONFLICT", "A database conflict occurred while saving spin results. Please retry.");
        }

        throw new InvalidOperationException("Spin attempt ended in an unexpected state.");
    }

    private async Task<Wheel?> LoadWheelForSpinAsync(string trimmedSlug, CancellationToken ct)
    {
        if (db.Database.IsSqlServer())
        {
            return await db.Wheels
                .FromSqlInterpolated($"SELECT * FROM [Wheels] WITH (REPEATABLEREAD, ROWLOCK) WHERE [Slug] = {trimmedSlug}")
                .AsNoTracking()
                .SingleOrDefaultAsync(ct);
        }

        return await db.Wheels.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Slug == trimmedSlug, ct);
    }

    private async Task<WheelVersion?> LoadActiveVersionForSpinAsync(Guid wheelId, CancellationToken ct)
    {
        if (db.Database.IsSqlServer())
        {
            return await db.WheelVersions
                .FromSqlInterpolated($"SELECT * FROM [WheelVersions] WITH (REPEATABLEREAD, ROWLOCK) WHERE [WheelId] = {wheelId} AND [Status] = 'Active'")
                .AsNoTracking()
                .SingleOrDefaultAsync(ct);
        }

        return await db.WheelVersions.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.WheelId == wheelId && x.Status == WheelVersionStatus.Active,
                ct);
    }

    private async Task<SpinResultDto?> TryResolveIdempotencyAsync(
        string trimmedSlug,
        string trimmedIdempotencyKey,
        string normalizedEmail,
        CancellationToken ct)
    {
        var existingSpin = await db.SpinHistories.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == trimmedIdempotencyKey, ct);

        if (existingSpin == null)
        {
            return null;
        }

        var targetWheel = await db.Wheels.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Slug == trimmedSlug, ct);

        if (targetWheel == null
            || existingSpin.WheelId != targetWheel.Id
            || !string.Equals(existingSpin.EmailNormalized, normalizedEmail, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConflictException(
                "IDEMPOTENCY_KEY_REUSED",
                "Idempotency key has already been used with different request parameters.");
        }

        return await BuildReplayResponseAsync(existingSpin, ct);
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

            var hasSnapshot = !string.IsNullOrWhiteSpace(spin.PrizeNameSnapshot);
            var prizeName = hasSnapshot
                ? spin.PrizeNameSnapshot!
                : SpinHistory.LegacyMissingPrizeNameFallback;
            var prizeDescription = hasSnapshot
                ? spin.PrizeDescriptionSnapshot
                : null;

            prizeDto = new SpinPrizeDto(
                spin.PrizeId.Value,
                prizeName,
                prizeDescription,
                plaintextKey,
                keyExpiresAtUtc);
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
        await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.AcceptAllChanges();
    }

    private static async Task RollbackTransactionAsync(IDbContextTransaction tx)
    {
        try
        {
            await tx.RollbackAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Ignore when the transaction has already completed or its underlying connection broke.
        }
        catch (DbException)
        {
            // Ignore connection-drop rollback errors so the original exception is preserved for retry/handling.
        }
    }

    private void CleanupAttemptState(
        PrizeKey? allocatedKey,
        SpinHistory? pendingSpin,
        WinnerLock? pendingWinnerLock)
    {
        if (pendingWinnerLock != null)
        {
            var lockEntry = db.Entry(pendingWinnerLock);
            if (lockEntry.State != EntityState.Detached)
            {
                lockEntry.State = EntityState.Detached;
            }
        }

        if (pendingSpin != null)
        {
            var spinEntry = db.Entry(pendingSpin);
            if (spinEntry.State != EntityState.Detached)
            {
                spinEntry.State = EntityState.Detached;
            }
        }

        if (allocatedKey != null)
        {
            var keyEntry = db.Entry(allocatedKey);
            if (keyEntry.State != EntityState.Detached)
            {
                if (keyEntry.State == EntityState.Modified)
                {
                    keyEntry.CurrentValues.SetValues(keyEntry.OriginalValues);
                }

                keyEntry.State = EntityState.Detached;
            }
        }
    }

    private static SpinUniqueConstraintConflict ClassifyUniqueConstraintViolation(DbUpdateException ex)
    {
        var sqlEx = ex.InnerException as SqlException ?? ex.GetBaseException() as SqlException;
        if (sqlEx == null)
        {
            return SpinUniqueConstraintConflict.None;
        }

        if (sqlEx.Errors.Count > 0)
        {
            foreach (SqlError error in sqlEx.Errors)
            {
                if (error.Number is 2601 or 2627)
                {
                    var classified = MatchSpinUniqueIndexName(error.Message);
                    if (classified != SpinUniqueConstraintConflict.None)
                    {
                        return classified;
                    }
                }
            }

            return SpinUniqueConstraintConflict.None;
        }

        return sqlEx.Number is 2601 or 2627
            ? MatchSpinUniqueIndexName(sqlEx.Message)
            : SpinUniqueConstraintConflict.None;
    }

    private static SpinUniqueConstraintConflict MatchSpinUniqueIndexName(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return SpinUniqueConstraintConflict.None;
        }

        if (ContainsExactIndexName(message, IdempotencyKeyIndexName))
        {
            return SpinUniqueConstraintConflict.IdempotencyKey;
        }

        if (ContainsExactIndexName(message, WinnerLockIndexName))
        {
            return SpinUniqueConstraintConflict.WinnerLock;
        }

        if (ContainsExactIndexName(message, SpinPrizeKeyIndexName))
        {
            return SpinUniqueConstraintConflict.PrizeKeyAssignment;
        }

        if (ContainsExactIndexName(message, ReceiptTokenIndexName))
        {
            return SpinUniqueConstraintConflict.ReceiptToken;
        }

        return SpinUniqueConstraintConflict.None;
    }

    private static bool ContainsExactIndexName(string message, string indexName)
    {
        var searchStart = 0;
        while (searchStart < message.Length)
        {
            var idx = message.IndexOf(indexName, searchStart, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            var beforeOk = idx == 0 || !IsIdentifierChar(message[idx - 1]);
            var afterIdx = idx + indexName.Length;
            var afterOk = afterIdx == message.Length || !IsIdentifierChar(message[afterIdx]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            searchStart = idx + indexName.Length;
        }

        return false;
    }

    private static bool IsIdentifierChar(char ch) =>
        char.IsAsciiLetterOrDigit(ch) || ch == '_';
}
