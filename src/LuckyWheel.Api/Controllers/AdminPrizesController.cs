using LuckyWheel.Application.Features.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuckyWheel.Api.Controllers;

[ApiController, Authorize(Policy = "AdminOnly"), Route("api/admin/prizes")]
public sealed class AdminPrizesController(IAdminManagementService service) : ControllerBase
{
    /// <summary>Tạo giải thưởng mới cho một vòng quay.</summary>
    [HttpPost]
    public async Task<ActionResult<PrizeDto>> Create(CreatePrizeRequest request, CancellationToken ct)
    { var result = await service.CreatePrizeAsync(request, ct); return CreatedAtAction(nameof(Get), new { id = result.Id }, result); }
    /// <summary>Xem chi tiết một giải thưởng.</summary>
    [HttpGet("{id:guid}")] public Task<PrizeDto> Get(Guid id, CancellationToken ct) => service.GetPrizeAsync(id, ct);

    /// <summary>Lấy danh sách và lọc Prize theo Wheel/RequiresKey.</summary>
    [HttpGet]
    public Task<PageResult<PrizeDto>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] bool? requiresKey = null, [FromQuery] Guid? wheelId = null, CancellationToken ct = default) =>
        service.GetPrizesAsync(page, pageSize, requiresKey, wheelId, ct);
    /// <summary>Cập nhật giải thưởng với RowVersion chống ghi đè.</summary>
    [HttpPut("{id:guid}")] public Task<PrizeDto> Update(Guid id, UpdatePrizeRequest request, CancellationToken ct) => service.UpdatePrizeAsync(id, request, ct);
}
