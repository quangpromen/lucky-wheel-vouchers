using System;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Application.Features.Admin;
using LuckyWheel.Application.Features.Admin.PrizeKeys;
using LuckyWheel.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuckyWheel.Api.Controllers;

[ApiController, Authorize(Policy = "AdminOnly")]
public sealed class AdminPrizeKeysController(
    IPrizeKeyService service,
    IPrizeKeyExpirationProcessor expirationProcessor) : ControllerBase
{
    /// <summary>Sinh một lô mã trúng thưởng (Prize Key) ngẫu nhiên an toàn cho giải thưởng yêu cầu key.</summary>
    [HttpPost("api/admin/prizes/{prizeId:guid}/keys/generate")]
    public async Task<ActionResult<GeneratePrizeKeysResponse>> Generate(
        Guid prizeId,
        GeneratePrizeKeysRequest request,
        CancellationToken ct)
    {
        var result = await service.GenerateKeysAsync(prizeId, request, ct);
        return Ok(result);
    }

    /// <summary>Lấy danh sách Prize Key có phân trang, giải mã mã trúng thưởng (Code) và lọc theo PrizeId / Status / Code.</summary>
    [HttpGet("api/admin/prize-keys")]
    public Task<PageResult<PrizeKeyDto>> List(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? prizeId = null,
        [FromQuery] PrizeKeyStatus? status = null,
        [FromQuery] string? code = null,
        CancellationToken ct = default) =>
        service.GetKeysAsync(pageNumber, pageSize, prizeId, status, code, ct);

    /// <summary>Xem chi tiết metadata của một Prize Key.</summary>
    [HttpGet("api/admin/prize-keys/{prizeKeyId:guid}")]
    public Task<PrizeKeyDto> Get(Guid prizeKeyId, CancellationToken ct) =>
        service.GetKeyByIdAsync(prizeKeyId, ct);

    /// <summary>Admin xác nhận đổi thưởng cho mã trúng thưởng (Redeem).</summary>
    [HttpPost("api/admin/prize-keys/{prizeKeyId:guid}/redeem")]
    public async Task<ActionResult<RedeemPrizeKeyResponse>> Redeem(
        Guid prizeKeyId,
        RedeemPrizeKeyRequest request,
        CancellationToken ct)
    {
        var result = await service.RedeemKeyAsync(prizeKeyId, request, ct);
        return Ok(result);
    }

    /// <summary>Admin hủy mã trúng thưởng (Cancel).</summary>
    [HttpPost("api/admin/prize-keys/{prizeKeyId:guid}/cancel")]
    public async Task<ActionResult<CancelPrizeKeyResponse>> Cancel(
        Guid prizeKeyId,
        CancelPrizeKeyRequest request,
        CancellationToken ct)
    {
        var result = await service.CancelKeyAsync(prizeKeyId, request, ct);
        return Ok(result);
    }

    /// <summary>Admin tạo mã thay thế (Replacement Key) cho key đã bị Hủy hoặc Hết hạn.</summary>
    [HttpPost("api/admin/prize-keys/{prizeKeyId:guid}/replacement")]
    public async Task<ActionResult<CreateReplacementPrizeKeyResponse>> CreateReplacement(
        Guid prizeKeyId,
        CancellationToken ct)
    {
        var result = await service.CreateReplacementKeyAsync(prizeKeyId, ct);
        return Ok(result);
    }

    /// <summary>Thao tác vận hành quét hết hạn các key Assigned quá hạn theo lô.</summary>
    [HttpPost("api/admin/prize-keys/expire-overdue")]
    public async Task<ActionResult<ExpirePrizeKeysResult>> ExpireOverdue(
        [FromQuery] int batchSize = 100,
        CancellationToken ct = default)
    {
        var result = await expirationProcessor.ProcessExpiredKeysBatchAsync(batchSize, ct);
        return Ok(result);
    }
}
