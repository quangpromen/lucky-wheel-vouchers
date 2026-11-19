using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Application.Features.Admin.PrizeKeys;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using LuckyWheel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LuckyWheel.Infrastructure.PrizeKeys;

public sealed class PrizeKeyExpirationProcessor(
    ApplicationDbContext db,
    IClock clock,
    ILogger<PrizeKeyExpirationProcessor> logger) : IPrizeKeyExpirationProcessor
{
    public async Task<ExpirePrizeKeysResult> ProcessExpiredKeysBatchAsync(
        int batchSize,
        CancellationToken ct = default)
    {
        if (batchSize < 1) batchSize = 100;
        if (batchSize > 1000) batchSize = 1000;

        var now = clock.UtcNow.UtcDateTime;

        // Retrieve candidate expired keys
        var candidateIds = await db.PrizeKeys
            .AsNoTracking()
            .Where(x => x.Status == PrizeKeyStatus.Assigned && x.ExpiresAtUtc.HasValue && x.ExpiresAtUtc.Value <= now)
            .OrderBy(x => x.ExpiresAtUtc)
            .Select(x => x.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (candidateIds.Count == 0)
        {
            return new ExpirePrizeKeysResult(0, 0, 0, []);
        }

        int successCount = 0;
        int failedCount = 0;
        var expiredKeyIds = new List<Guid>();

        foreach (var keyId in candidateIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var strategy = db.Database.CreateExecutionStrategy();
                var processed = await strategy.ExecuteAsync(async () =>
                {
                    await using var tx = await db.Database.BeginTransactionAsync(ct);

                    var key = await db.PrizeKeys.SingleOrDefaultAsync(x => x.Id == keyId, ct);
                    if (key == null || key.Status != PrizeKeyStatus.Assigned || !key.ExpiresAtUtc.HasValue || key.ExpiresAtUtc.Value > clock.UtcNow.UtcDateTime)
                    {
                        // Key state already changed concurrently or deadline not reached
                        return false;
                    }

                    var expireTime = clock.UtcNow.UtcDateTime;
                    key.Expire(expireTime);

                    var audit = new AuditLog(
                        null,
                        AuditAction.Expired,
                        "PrizeKey",
                        key.Id,
                        $"Automatically expired prize key {key.Id}.",
                        JsonSerializer.Serialize(new { prizeKeyId = key.Id, expiredAtUtc = expireTime }),
                        expireTime);

                    db.AuditLogs.Add(audit);

                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return true;
                });

                if (processed)
                {
                    successCount++;
                    expiredKeyIds.Add(keyId);
                }
                else
                {
                    failedCount++;
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                failedCount++;
                logger.LogWarning("Concurrency conflict while expiring prize key {PrizeKeyId}. Key might have been redeemed or cancelled concurrently.", keyId);
            }
            catch (Exception ex)
            {
                failedCount++;
                logger.LogError(ex, "Unexpected error occurred while expiring prize key {PrizeKeyId}.", keyId);
            }
        }

        return new ExpirePrizeKeysResult(candidateIds.Count, successCount, failedCount, expiredKeyIds);
    }
}
