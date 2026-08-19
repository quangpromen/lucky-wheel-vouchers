using LuckyWheel.Application.Features.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuckyWheel.Api.Controllers;

[ApiController, Authorize(Policy = "AdminOnly"), Route("api/admin/wheel-versions")]
public sealed class AdminWheelVersionsController(IAdminManagementService service) : ControllerBase
{
    /// <summary>Xem chi tiết Version và toàn bộ các ô giải thưởng.</summary>
    [HttpGet("{id:guid}")] public Task<WheelVersionDto> Get(Guid id, CancellationToken ct) => service.GetVersionAsync(id, ct);

    /// <summary>Cập nhật lịch chạy của Draft Version.</summary>
    [HttpPut("{id:guid}")] public Task<WheelVersionDto> Update(Guid id, UpdateDraftWheelVersionRequest request, CancellationToken ct) => service.UpdateDraftVersionAsync(id, request, ct);

    /// <summary>Kích hoạt Draft Version sang Active.</summary>
    [HttpPost("{id:guid}/activate")]
    public Task<WheelVersionDto> Activate(Guid id, ActivateDraftWheelVersionRequest request, CancellationToken ct) => service.ActivateVersionAsync(id, request, ct);

    /// <summary>Đóng Active Version sang Closed.</summary>
    [HttpPost("{id:guid}/close")]
    public Task<WheelVersionDto> Close(Guid id, CloseActiveWheelVersionRequest request, CancellationToken ct) => service.CloseVersionAsync(id, request, ct);

    /// <summary>Thêm ô giải thưởng hoặc ô không trúng vào Draft Version.</summary>
    [HttpPost("{versionId:guid}/prizes")]
    public async Task<ActionResult<WheelVersionPrizeDto>> AddPrize(Guid versionId, CreateWheelVersionPrizeRequest request, CancellationToken ct)
    { var result = await service.AddVersionPrizeAsync(versionId, request, ct); return Created($"/api/admin/wheel-versions/{versionId}/prizes/{result.Id}", result); }

    /// <summary>Cập nhật weight, thứ tự, màu và ảnh của một ô.</summary>
    [HttpPut("{versionId:guid}/prizes/{id:guid}")]
    public Task<WheelVersionPrizeDto> UpdatePrize(Guid versionId, Guid id, UpdateWheelVersionPrizeRequest request, CancellationToken ct) => service.UpdateVersionPrizeAsync(versionId, id, request, ct);

    /// <summary>Xóa một ô khỏi Draft Version.</summary>
    [HttpDelete("{versionId:guid}/prizes/{id:guid}")]
    public async Task<IActionResult> DeletePrize(Guid versionId, Guid id, [FromQuery] string rowVersion, CancellationToken ct)
    { await service.DeleteVersionPrizeAsync(versionId, id, rowVersion, ct); return NoContent(); }

    /// <summary>Sắp xếp lại toàn bộ các ô trong Draft Version.</summary>
    [HttpPut("{versionId:guid}/prizes/reorder")]
    public Task<IReadOnlyList<WheelVersionPrizeDto>> Reorder(Guid versionId, ReorderWheelVersionPrizesRequest request, CancellationToken ct) => service.ReorderVersionPrizesAsync(versionId, request, ct);
}
