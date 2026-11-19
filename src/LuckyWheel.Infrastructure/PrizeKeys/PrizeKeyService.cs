using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Application.Common.Authentication;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Application.Common.Validation;
using LuckyWheel.Application.Features.Admin;
using LuckyWheel.Application.Features.Admin.PrizeKeys;
using LuckyWheel.Application.Features.Public;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using LuckyWheel.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LuckyWheel.Infrastructure.PrizeKeys;

public sealed class PrizeKeyService(
    ApplicationDbContext db,
    IPrizeKeyGenerator generator,
    IPrizeKeyProtector protector,
    IClock clock,
    ICurrentAdminContext adminContext) : IPrizeKeyService
{
    public async Task<GeneratePrizeKeysResponse> GenerateKeysAsync(
        Guid prizeId,
        GeneratePrizeKeysRequest request,
        CancellationToken ct = default)
    {
        if (request.Quantity is < 1 or > 1000)
            ValidationResult.Failure("quantity", "Quantity must be between 1 and 1000.").ThrowIfInvalid();

        var prize = await db.Prizes.SingleOrDefaultAsync(x => x.Id == prizeId, ct)
            ?? throw new NotFoundException("Prize", prizeId.ToString());

        if (!prize.RequiresKey)
            throw new BusinessRuleViolationException("PRIZE_DOES_NOT_REQUIRE_KEY", "Prize does not require keys.");

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var createdAtUtc = clock.UtcNow.UtcDateTime;
            var generatedKeys = new List<PrizeKey>(request.Quantity);
            var batchHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < request.Quantity; i++)
            {
                string codeHash;
                ProtectedPrizeKey protectedKey;
                int retries = 0;
                const int maxRetries = 10;

                while (true)
                {
                    var plaintext = generator.GenerateKey();
                    protectedKey = protector.Protect(plaintext);
                    codeHash = protectedKey.CodeHash;

                    if (!batchHashes.Contains(codeHash) && !await db.PrizeKeys.AnyAsync(x => x.CodeHash == codeHash, ct))
                    {
                        batchHashes.Add(codeHash);
                        break;
                    }

                    retries++;
                    if (retries > maxRetries)
                        throw new InvalidOperationException("Failed to generate unique prize key after multiple attempts. Operation aborted.");
                }

                var keyEntity = new PrizeKey(prizeId, codeHash, protectedKey.EncryptedCode, protectedKey.EncryptionNonce, protectedKey.EncryptionTag, createdAtUtc);
                generatedKeys.Add(keyEntity);
            }

            db.PrizeKeys.AddRange(generatedKeys);

            var audit = new AuditLog(
                adminContext.AdminId,
                AuditAction.Created,
                "PrizeKey",
                prizeId,
                $"Generated {request.Quantity} prize keys for prize {prizeId}.",
                JsonSerializer.Serialize(new { prizeId, quantity = request.Quantity }),
                createdAtUtc);
            db.AuditLogs.Add(audit);

            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
            {
                throw new ConflictException("One or more generated prize keys collided with existing records. Please retry.");
            }

            return new GeneratePrizeKeysResponse(prizeId, request.Quantity, PrizeKeyStatus.Available.ToString(), createdAtUtc);
        });
    }

    public async Task<PageResult<PrizeKeyDto>> GetKeysAsync(
        int pageNumber,
        int pageSize,
        Guid? prizeId,
        PrizeKeyStatus? status,
        string? code = null,
        CancellationToken ct = default)
    {
        AdminValidation.ValidatePage(pageNumber, pageSize);

        var query = db.PrizeKeys.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(code))
        {
            var codeHash = protector.ComputeHash(code.Trim());
            query = query.Where(x => x.CodeHash == codeHash);
        }
        if (prizeId.HasValue)
            query = query.Where(x => x.PrizeId == prizeId.Value);
        if (status.HasValue)
            query = query.Where(x => x.Status == status.Value);

        var total = await query.CountAsync(ct);
        var rawItems = await (
            from k in query
            join p in db.Prizes.AsNoTracking() on k.PrizeId equals p.Id
            orderby k.CreatedAtUtc descending
            select new
            {
                k.Id,
                k.PrizeId,
                PrizeName = p.Name,
                k.EncryptedCode,
                k.EncryptionNonce,
                k.EncryptionTag,
                Status = k.Status.ToString(),
                k.CreatedAtUtc,
                k.AssignedAtUtc,
                k.ExpiresAtUtc,
                k.RedeemedAtUtc,
                k.CancelledAtUtc,
                k.AssignedSpinId,
                k.ReplacesPrizeKeyId,
                k.ExpiredAtUtc
            }
        ).Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var items = rawItems.Select(k => new PrizeKeyDto(
            k.Id,
            k.PrizeId,
            k.PrizeName,
            protector.Unprotect(k.EncryptedCode, k.EncryptionNonce, k.EncryptionTag),
            k.Status,
            k.CreatedAtUtc,
            k.AssignedAtUtc,
            k.ExpiresAtUtc,
            k.RedeemedAtUtc,
            k.CancelledAtUtc,
            k.AssignedSpinId,
            k.ReplacesPrizeKeyId,
            k.ExpiredAtUtc
        )).ToList();

        return new PageResult<PrizeKeyDto>(items, pageNumber, pageSize, total);
    }

    public async Task<PrizeKeyDto> GetKeyByIdAsync(
        Guid prizeKeyId,
        CancellationToken ct = default)
    {
        var raw = await (
            from k in db.PrizeKeys.AsNoTracking()
            join p in db.Prizes.AsNoTracking() on k.PrizeId equals p.Id
            where k.Id == prizeKeyId
            select new
            {
                k.Id,
                k.PrizeId,
                PrizeName = p.Name,
                k.EncryptedCode,
                k.EncryptionNonce,
                k.EncryptionTag,
                Status = k.Status.ToString(),
                k.CreatedAtUtc,
                k.AssignedAtUtc,
                k.ExpiresAtUtc,
                k.RedeemedAtUtc,
                k.CancelledAtUtc,
                k.AssignedSpinId,
                k.ReplacesPrizeKeyId,
                k.ExpiredAtUtc
            }
        ).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("PrizeKey", prizeKeyId.ToString());

        var code = protector.Unprotect(raw.EncryptedCode, raw.EncryptionNonce, raw.EncryptionTag);

        return new PrizeKeyDto(
            raw.Id,
            raw.PrizeId,
            raw.PrizeName,
            code,
            raw.Status,
            raw.CreatedAtUtc,
            raw.AssignedAtUtc,
            raw.ExpiresAtUtc,
            raw.RedeemedAtUtc,
            raw.CancelledAtUtc,
            raw.AssignedSpinId,
            raw.ReplacesPrizeKeyId,
            raw.ExpiredAtUtc);
    }

    public async Task<RedeemPrizeKeyResponse> RedeemKeyAsync(
        Guid prizeKeyId,
        RedeemPrizeKeyRequest request,
        CancellationToken ct = default)
    {
        if (request?.Note != null && request.Note.Length > 1000)
            ValidationResult.Failure("note", "Note must not exceed 1000 characters.").ThrowIfInvalid();

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var key = await db.PrizeKeys.SingleOrDefaultAsync(x => x.Id == prizeKeyId, ct)
                ?? throw new NotFoundException("PrizeKey", prizeKeyId.ToString());

            if (key.Status != PrizeKeyStatus.Assigned)
                throw new ConflictException("PRIZE_KEY_NOT_ASSIGNED", $"Only Assigned prize keys can be redeemed. Current status is {key.Status}.");

            var now = clock.UtcNow.UtcDateTime;
            if (key.ExpiresAtUtc.HasValue && now >= key.ExpiresAtUtc.Value)
                throw new ConflictException("PRIZE_KEY_EXPIRED", "Prize key is already expired and cannot be redeemed.");

            if (!key.AssignedSpinId.HasValue)
                throw new ConflictException("PRIZE_KEY_STATE_CONFLICT", "Prize key is not assigned to a valid spin.");

            var note = string.IsNullOrWhiteSpace(request?.Note) ? null : request.Note.Trim();
            var adminId = adminContext.AdminId ?? Guid.Empty;

            key.Redeem(now);

            var redemption = new PrizeRedemption(
                key.AssignedSpinId.Value,
                key.Id,
                adminId,
                now,
                note);
            db.PrizeRedemptions.Add(redemption);

            var audit = new AuditLog(
                adminContext.AdminId,
                AuditAction.Redeemed,
                "PrizeKey",
                key.Id,
                $"Redeemed prize key {key.Id}.",
                JsonSerializer.Serialize(new { prizeKeyId = key.Id, spinId = key.AssignedSpinId, note }),
                now);
            db.AuditLogs.Add(audit);

            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ConflictException("PRIZE_KEY_STATE_CONFLICT", "Prize key state was modified by another operation.");
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
            {
                throw new ConflictException("PRIZE_KEY_ALREADY_REDEEMED", "Prize key or spin has already been redeemed.");
            }

            return new RedeemPrizeKeyResponse(
                key.Id,
                key.AssignedSpinId.Value,
                key.Status.ToString(),
                now,
                note);
        });
    }

    public async Task<CancelPrizeKeyResponse> CancelKeyAsync(
        Guid prizeKeyId,
        CancelPrizeKeyRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request?.Reason))
            ValidationResult.Failure("reason", "Reason is required for cancellation.").ThrowIfInvalid();

        var reason = request!.Reason!.Trim();
        if (reason.Length > 1000)
            ValidationResult.Failure("reason", "Reason must not exceed 1000 characters.").ThrowIfInvalid();

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var key = await db.PrizeKeys.SingleOrDefaultAsync(x => x.Id == prizeKeyId, ct)
                ?? throw new NotFoundException("PrizeKey", prizeKeyId.ToString());

            if (key.Status != PrizeKeyStatus.Assigned)
                throw new ConflictException("PRIZE_KEY_NOT_ASSIGNED", $"Only Assigned prize keys can be cancelled. Current status is {key.Status}.");

            var now = clock.UtcNow.UtcDateTime;
            key.Cancel(now);

            var audit = new AuditLog(
                adminContext.AdminId,
                AuditAction.Cancelled,
                "PrizeKey",
                key.Id,
                $"Cancelled prize key {key.Id}. Reason: {reason}",
                JsonSerializer.Serialize(new { prizeKeyId = key.Id, spinId = key.AssignedSpinId, reason }),
                now);
            db.AuditLogs.Add(audit);

            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ConflictException("PRIZE_KEY_STATE_CONFLICT", "Prize key state was modified by another operation.");
            }

            return new CancelPrizeKeyResponse(
                key.Id,
                key.AssignedSpinId ?? Guid.Empty,
                key.Status.ToString(),
                now,
                reason);
        });
    }

    public async Task<CreateReplacementPrizeKeyResponse> CreateReplacementKeyAsync(
        Guid prizeKeyId,
        CancellationToken ct = default)
    {
        var originalKey = await db.PrizeKeys.SingleOrDefaultAsync(x => x.Id == prizeKeyId, ct)
            ?? throw new NotFoundException("PrizeKey", prizeKeyId.ToString());

        if (originalKey.Status != PrizeKeyStatus.Cancelled && originalKey.Status != PrizeKeyStatus.Expired)
            throw new ConflictException("PRIZE_KEY_INVALID_STATUS_FOR_REPLACEMENT", $"Only Cancelled or Expired prize keys can be replaced. Current status is {originalKey.Status}.");

        if (!originalKey.AssignedSpinId.HasValue)
            throw new ConflictException("PRIZE_KEY_NOT_ASSIGNED_TO_SPIN", "Original key was not assigned to a valid spin history.");

        var alreadyReplaced = await db.PrizeKeys.AnyAsync(x => x.ReplacesPrizeKeyId == prizeKeyId, ct);
        if (alreadyReplaced)
            throw new ConflictException("PRIZE_KEY_REPLACEMENT_ALREADY_EXISTS", "A replacement key has already been created for this prize key.");

        var spin = await db.SpinHistories.AsNoTracking().SingleOrDefaultAsync(x => x.Id == originalKey.AssignedSpinId.Value, ct)
            ?? throw new NotFoundException("SpinHistory", originalKey.AssignedSpinId.Value.ToString());

        var version = await db.WheelVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == spin.WheelVersionId, ct);
        var claimDurationMinutes = version?.ClaimDurationMinutes ?? 60;
        if (claimDurationMinutes <= 0) claimDurationMinutes = 60;

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Re-verify replacement existence inside transaction
            if (await db.PrizeKeys.AnyAsync(x => x.ReplacesPrizeKeyId == prizeKeyId, ct))
                throw new ConflictException("PRIZE_KEY_REPLACEMENT_ALREADY_EXISTS", "A replacement key has already been created for this prize key.");

            string plaintextCode;
            ProtectedPrizeKey protectedKey;
            int retries = 0;
            const int maxRetries = 10;

            while (true)
            {
                plaintextCode = generator.GenerateKey();
                protectedKey = protector.Protect(plaintextCode);

                if (!await db.PrizeKeys.AnyAsync(x => x.CodeHash == protectedKey.CodeHash, ct))
                {
                    break;
                }

                retries++;
                if (retries > maxRetries)
                    throw new InvalidOperationException("Failed to generate unique replacement prize key after multiple attempts.");
            }

            var assignedAtUtc = clock.UtcNow.UtcDateTime;
            var expiresAtUtc = assignedAtUtc.AddMinutes(claimDurationMinutes);

            var replacementKey = PrizeKey.CreateReplacement(
                originalKey.PrizeId,
                spin.Id,
                protectedKey.CodeHash,
                protectedKey.EncryptedCode,
                protectedKey.EncryptionNonce,
                protectedKey.EncryptionTag,
                assignedAtUtc,
                expiresAtUtc,
                originalKey.Id);

            db.PrizeKeys.Add(replacementKey);

            var audit = new AuditLog(
                adminContext.AdminId,
                AuditAction.Created,
                "PrizeKey",
                replacementKey.Id,
                $"Created replacement prize key {replacementKey.Id} for original key {originalKey.Id}.",
                JsonSerializer.Serialize(new
                {
                    originalKeyId = originalKey.Id,
                    replacementKeyId = replacementKey.Id,
                    spinId = spin.Id,
                    prizeId = originalKey.PrizeId
                }),
                assignedAtUtc);
            db.AuditLogs.Add(audit);

            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
            {
                throw new ConflictException("PRIZE_KEY_REPLACEMENT_ALREADY_EXISTS", "A replacement key has already been created for this prize key or a code collision occurred.");
            }

            return new CreateReplacementPrizeKeyResponse(
                replacementKey.Id,
                originalKey.Id,
                originalKey.PrizeId,
                spin.Id,
                replacementKey.Status.ToString(),
                assignedAtUtc,
                expiresAtUtc,
                plaintextCode);
        });
    }

    public async Task<PageResult<AdminSpinHistoryDto>> GetWheelSpinsAsync(
        Guid wheelId,
        AdminSpinHistoryFilter filter,
        CancellationToken ct = default)
    {
        AdminValidation.ValidatePage(filter.PageNumber, filter.PageSize);

        var wheelExists = await db.Wheels.AsNoTracking().AnyAsync(x => x.Id == wheelId, ct);
        if (!wheelExists)
            throw new NotFoundException("Wheel", wheelId.ToString());

        var query = db.SpinHistories.AsNoTracking().Where(x => x.WheelId == wheelId);

        if (filter.WheelVersionId.HasValue)
            query = query.Where(x => x.WheelVersionId == filter.WheelVersionId.Value);

        if (filter.PrizeId.HasValue)
            query = query.Where(x => x.PrizeId == filter.PrizeId.Value);

        if (filter.Result.HasValue)
            query = query.Where(x => x.Result == filter.Result.Value);

        if (filter.Status.HasValue)
            query = query.Where(x => x.Status == filter.Status.Value);

        if (filter.FromUtc.HasValue)
            query = query.Where(x => x.CreatedAtUtc >= filter.FromUtc.Value);

        if (filter.ToUtc.HasValue)
            query = query.Where(x => x.CreatedAtUtc <= filter.ToUtc.Value);

        if (!string.IsNullOrWhiteSpace(filter.ReceiptToken))
        {
            var receipt = filter.ReceiptToken.Trim();
            query = query.Where(x => x.ReceiptToken == receipt);
        }

        if (!string.IsNullOrWhiteSpace(filter.Email))
        {
            if (PublicSpinValidation.TryNormalizeEmail(filter.Email, out var normalized))
            {
                query = query.Where(x => x.EmailNormalized == normalized);
            }
            else
            {
                query = query.Where(_ => false);
            }
        }

        var total = await query.CountAsync(ct);

        query = filter.SortBy?.ToLowerInvariant() switch
        {
            "createdatasc" or "asc" => query.OrderBy(x => x.CreatedAtUtc),
            _ => query.OrderByDescending(x => x.CreatedAtUtc)
        };

        var rawItems = await (
            from s in query
            join v in db.WheelVersions.AsNoTracking() on s.WheelVersionId equals v.Id
            join p in db.Prizes.AsNoTracking() on s.PrizeId equals p.Id into prizeJoin
            from p in prizeJoin.DefaultIfEmpty()
            join k in db.PrizeKeys.AsNoTracking() on s.PrizeKeyId equals k.Id into keyJoin
            from k in keyJoin.DefaultIfEmpty()
            select new
            {
                s.Id,
                s.WheelId,
                s.WheelVersionId,
                VersionNumber = v.VersionNumber,
                s.EmailOriginal,
                Result = s.Result.ToString(),
                Status = s.Status.ToString(),
                s.PrizeId,
                PrizeName = p != null ? p.Name : null,
                s.PrizeKeyId,
                PrizeKeyStatus = k != null ? k.Status.ToString() : null,
                s.CreatedAtUtc,
                s.CancelledAtUtc,
                s.CancellationReason
            }
        ).Skip((filter.PageNumber - 1) * filter.PageSize).Take(filter.PageSize).ToListAsync(ct);

        var items = rawItems.Select(x => new AdminSpinHistoryDto(
            x.Id,
            x.WheelId,
            x.WheelVersionId,
            x.VersionNumber,
            MaskEmail(x.EmailOriginal),
            x.Result,
            x.Status,
            x.PrizeId,
            x.PrizeName,
            x.PrizeKeyId,
            x.PrizeKeyStatus,
            x.CreatedAtUtc,
            x.CancelledAtUtc,
            x.CancellationReason
        )).ToList();

        return new PageResult<AdminSpinHistoryDto>(items, filter.PageNumber, filter.PageSize, total);
    }

    private static string MaskEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return string.Empty;
        var parts = email.Split('@');
        if (parts.Length != 2) return "***";
        var local = parts[0];
        var domain = parts[1];
        if (local.Length <= 2)
            return $"{local[0]}*@{domain}";
        return $"{local[0]}***{local[^1]}@{domain}";
    }
}
