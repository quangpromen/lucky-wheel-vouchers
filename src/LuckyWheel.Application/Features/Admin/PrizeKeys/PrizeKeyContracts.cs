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
    Guid? AssignedSpinId,
    Guid? ReplacesPrizeKeyId = null,
    DateTime? ExpiredAtUtc = null);

public sealed record RedeemPrizeKeyRequest(string? Note);

public sealed record RedeemPrizeKeyResponse(
    Guid PrizeKeyId,
    Guid SpinId,
    string Status,
    DateTime RedeemedAtUtc,
    string? Note);

public sealed record CancelPrizeKeyRequest(string Reason);

public sealed record CancelPrizeKeyResponse(
    Guid PrizeKeyId,
    Guid SpinId,
    string Status,
    DateTime CancelledAtUtc,
    string Reason);

public sealed record CreateReplacementPrizeKeyResponse(
    Guid NewPrizeKeyId,
    Guid OriginalPrizeKeyId,
    Guid PrizeId,
    Guid SpinId,
    string Status,
    DateTime AssignedAtUtc,
    DateTime ExpiresAtUtc,
    string? Code = null);

public sealed record ExpirePrizeKeysResult(
    int ProcessedCount,
    int SuccessCount,
    int FailedCount,
    IReadOnlyList<Guid> ExpiredKeyIds);

public sealed record AdminSpinHistoryDto(
    Guid Id,
    Guid WheelId,
    Guid WheelVersionId,
    int VersionNumber,
    string MaskedEmail,
    string Result,
    string Status,
    Guid? PrizeId,
    string? PrizeName,
    Guid? PrizeKeyId,
    string? PrizeKeyStatus,
    DateTime CreatedAtUtc,
    DateTime? CancelledAtUtc,
    string? CancellationReason);

public sealed record AdminSpinHistoryFilter(
    int PageNumber = 1,
    int PageSize = 20,
    Guid? WheelVersionId = null,
    Guid? PrizeId = null,
    SpinResult? Result = null,
    SpinStatus? Status = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    string? ReceiptToken = null,
    string? Email = null,
    string? SortBy = null);

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

    Task<RedeemPrizeKeyResponse> RedeemKeyAsync(
        Guid prizeKeyId,
        RedeemPrizeKeyRequest request,
        CancellationToken ct = default);

    Task<CancelPrizeKeyResponse> CancelKeyAsync(
        Guid prizeKeyId,
        CancelPrizeKeyRequest request,
        CancellationToken ct = default);

    Task<CreateReplacementPrizeKeyResponse> CreateReplacementKeyAsync(
        Guid prizeKeyId,
        CancellationToken ct = default);

    Task<PageResult<AdminSpinHistoryDto>> GetWheelSpinsAsync(
        Guid wheelId,
        AdminSpinHistoryFilter filter,
        CancellationToken ct = default);
}

public interface IPrizeKeyExpirationProcessor
{
    Task<ExpirePrizeKeysResult> ProcessExpiredKeysBatchAsync(
        int batchSize,
        CancellationToken ct = default);
}
