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
                k.AssignedSpinId
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
            k.AssignedSpinId
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
                k.AssignedSpinId
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
            raw.AssignedSpinId);
    }
}
