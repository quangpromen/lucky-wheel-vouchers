using System;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Application.Features.Admin;
using LuckyWheel.Application.Features.Admin.PrizeKeys;
using LuckyWheel.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuckyWheel.Api.Controllers;

[ApiController, Authorize(Policy = "AdminOnly"), Route("api/admin/wheels")]
public sealed class AdminWheelsController(
    IAdminManagementService service,
    IPrizeKeyService prizeKeyService) : ControllerBase
{
    /// <summary>Tạo một vòng quay mới (slug phải duy nhất).</summary>
    [HttpPost]
    public async Task<ActionResult<WheelDto>> Create(CreateWheelRequest request, CancellationToken ct)
    { var result = await service.CreateWheelAsync(request, ct); return CreatedAtAction(nameof(Get), new { id = result.Id }, result); }

    /// <summary>Xem chi tiết một vòng quay.</summary>
    [HttpGet("{id:guid}")]
    public Task<WheelDto> Get(Guid id, CancellationToken ct) => service.GetWheelAsync(id, ct);

    /// <summary>Lấy danh sách vòng quay có phân trang.</summary>
    [HttpGet]
    public Task<PageResult<WheelDto>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) => service.GetWheelsAsync(page, pageSize, ct);

    /// <summary>Cập nhật thông tin cơ bản của vòng quay.</summary>
    [HttpPut("{id:guid}")]
    public Task<WheelDto> Update(Guid id, UpdateWheelRequest request, CancellationToken ct) => service.UpdateWheelAsync(id, request, ct);

    /// <summary>Tạo Draft Version mới, tự tăng VersionNumber.</summary>
    [HttpPost("{wheelId:guid}/versions")]
    public async Task<ActionResult<WheelVersionDto>> CreateVersion(Guid wheelId, CreateDraftWheelVersionRequest request, CancellationToken ct)
    { var result = await service.CreateDraftVersionAsync(wheelId, request, ct); return CreatedAtAction(nameof(AdminWheelVersionsController.Get), "AdminWheelVersions", new { id = result.Id }, result); }

    /// <summary>Lấy danh sách Version của một vòng quay.</summary>
    [HttpGet("{wheelId:guid}/versions")]
    public Task<PageResult<WheelVersionDto>> Versions(Guid wheelId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) => service.GetVersionsAsync(wheelId, page, pageSize, ct);

    /// <summary>Lấy danh sách lịch sử quay thưởng của một vòng quay với phân trang và bộ lọc an toàn.</summary>
    [HttpGet("{wheelId:guid}/spins")]
    public Task<PageResult<AdminSpinHistoryDto>> GetSpins(
        Guid wheelId,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? wheelVersionId = null,
        [FromQuery] Guid? prizeId = null,
        [FromQuery] SpinResult? result = null,
        [FromQuery] SpinStatus? status = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] string? receiptToken = null,
        [FromQuery] string? email = null,
        [FromQuery] string? sortBy = null,
        CancellationToken ct = default)
    {
        var filter = new AdminSpinHistoryFilter(
            pageNumber,
            pageSize,
            wheelVersionId,
            prizeId,
            result,
            status,
            fromUtc,
            toUtc,
            receiptToken,
            email,
            sortBy);

        return prizeKeyService.GetWheelSpinsAsync(wheelId, filter, ct);
    }
}
