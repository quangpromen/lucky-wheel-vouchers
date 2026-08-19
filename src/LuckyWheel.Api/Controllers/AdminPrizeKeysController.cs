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
public sealed class AdminPrizeKeysController(IPrizeKeyService service) : ControllerBase
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
}
