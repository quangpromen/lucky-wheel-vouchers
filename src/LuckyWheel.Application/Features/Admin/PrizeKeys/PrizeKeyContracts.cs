using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Domain.Enums;

namespace LuckyWheel.Application.Features.Admin.PrizeKeys;

public sealed record GeneratePrizeKeysRequest(int Quantity);

public sealed record GeneratePrizeKeysResponse(
    Guid PrizeId,
    int GeneratedCount,
    string Status,
    DateTime CreatedAtUtc);

public sealed record PrizeKeyDto(
    Guid Id,
    Guid PrizeId,
    string PrizeName,
    string Code,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? AssignedAtUtc,
    DateTime? ExpiresAtUtc,
    DateTime? RedeemedAtUtc,
    DateTime? CancelledAtUtc,
    Guid? AssignedSpinId);

public interface IPrizeKeyService
{
    Task<GeneratePrizeKeysResponse> GenerateKeysAsync(
        Guid prizeId,
        GeneratePrizeKeysRequest request,
        CancellationToken ct = default);

    Task<PageResult<PrizeKeyDto>> GetKeysAsync(
        int pageNumber,
        int pageSize,
        Guid? prizeId,
        PrizeKeyStatus? status,
        string? code = null,
        CancellationToken ct = default);

    Task<PrizeKeyDto> GetKeyByIdAsync(
        Guid prizeKeyId,
        CancellationToken ct = default);
}
