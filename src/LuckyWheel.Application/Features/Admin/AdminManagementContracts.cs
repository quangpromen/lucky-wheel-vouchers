using LuckyWheel.Application.Common.Validation;

namespace LuckyWheel.Application.Features.Admin;

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record WheelDto(Guid Id, string Name, string Slug, string? Description, string Terms, bool IsEnabled,
    DateTime CreatedAtUtc, DateTime? UpdatedAtUtc);
public sealed record CreateWheelRequest(string? Name, string? Slug, string? Description, string? Terms);
public sealed record UpdateWheelRequest(string? Name, string? Slug, string? Description, string? Terms);

public sealed record PrizeDto(Guid Id, Guid WheelId, string Name, string? Description, string? ImageUrl,
    bool RequiresKey, int TotalQuantity, bool IsEnabled, string RowVersion, DateTime CreatedAtUtc, DateTime? UpdatedAtUtc);
public sealed record CreatePrizeRequest(Guid WheelId, string? Name, string? Description, string? ImageUrl,
    bool RequiresKey, int TotalQuantity);
public sealed record UpdatePrizeRequest(string? Name, string? Description, string? ImageUrl,
    bool RequiresKey, int TotalQuantity, string? RowVersion);

public sealed record WheelVersionPrizeDto(Guid Id, Guid WheelVersionId, Guid? PrizeId, bool IsNoPrize,
    int Weight, int DisplayOrder, string Color, string? ImageUrl, string RowVersion);
public sealed record WheelVersionDto(Guid Id, Guid WheelId, int VersionNumber, string Status, DateTime StartAtUtc,
    DateTime EndAtUtc, int ClaimDurationMinutes, string RowVersion, IReadOnlyList<WheelVersionPrizeDto> Prizes,
    DateTime CreatedAtUtc, DateTime? UpdatedAtUtc);
public sealed record CreateDraftWheelVersionRequest(DateTime StartAtUtc, DateTime EndAtUtc, int ClaimDurationMinutes);
public sealed record UpdateDraftWheelVersionRequest(DateTime StartAtUtc, DateTime EndAtUtc,
    int ClaimDurationMinutes, string? RowVersion);
public sealed record CreateWheelVersionPrizeRequest(Guid? PrizeId, bool IsNoPrize, int Weight,
    int DisplayOrder, string? Color, string? ImageUrl);
public sealed record UpdateWheelVersionPrizeRequest(int Weight, int DisplayOrder, string? Color,
    string? ImageUrl, string? RowVersion);
public sealed record ReorderWheelVersionPrizeItem(Guid Id, int DisplayOrder, string? RowVersion);
public sealed record ReorderWheelVersionPrizesRequest(IReadOnlyList<ReorderWheelVersionPrizeItem>? Items);

public sealed record ActivateDraftWheelVersionRequest(string? RowVersion);
public sealed record CloseActiveWheelVersionRequest(string? RowVersion);

public interface IAdminManagementService
{
    Task<WheelDto> CreateWheelAsync(CreateWheelRequest request, CancellationToken ct);
    Task<WheelDto> GetWheelAsync(Guid id, CancellationToken ct);
    Task<PageResult<WheelDto>> GetWheelsAsync(int page, int pageSize, CancellationToken ct);
    Task<WheelDto> UpdateWheelAsync(Guid id, UpdateWheelRequest request, CancellationToken ct);
    Task<PrizeDto> CreatePrizeAsync(CreatePrizeRequest request, CancellationToken ct);
    Task<PrizeDto> GetPrizeAsync(Guid id, CancellationToken ct);
    Task<PageResult<PrizeDto>> GetPrizesAsync(int page, int pageSize, bool? requiresKey, Guid? wheelId, CancellationToken ct);
    Task<PrizeDto> UpdatePrizeAsync(Guid id, UpdatePrizeRequest request, CancellationToken ct);
    Task<WheelVersionDto> CreateDraftVersionAsync(Guid wheelId, CreateDraftWheelVersionRequest request, CancellationToken ct);
    Task<WheelVersionDto> GetVersionAsync(Guid id, CancellationToken ct);
    Task<PageResult<WheelVersionDto>> GetVersionsAsync(Guid wheelId, int page, int pageSize, CancellationToken ct);
    Task<WheelVersionDto> UpdateDraftVersionAsync(Guid id, UpdateDraftWheelVersionRequest request, CancellationToken ct);
    Task<WheelVersionDto> ActivateVersionAsync(Guid versionId, ActivateDraftWheelVersionRequest request, CancellationToken ct);
    Task<WheelVersionDto> CloseVersionAsync(Guid versionId, CloseActiveWheelVersionRequest request, CancellationToken ct);
    Task<WheelVersionPrizeDto> AddVersionPrizeAsync(Guid versionId, CreateWheelVersionPrizeRequest request, CancellationToken ct);
    Task<WheelVersionPrizeDto> UpdateVersionPrizeAsync(Guid versionId, Guid id, UpdateWheelVersionPrizeRequest request, CancellationToken ct);
    Task DeleteVersionPrizeAsync(Guid versionId, Guid id, string rowVersion, CancellationToken ct);
    Task<IReadOnlyList<WheelVersionPrizeDto>> ReorderVersionPrizesAsync(Guid versionId, ReorderWheelVersionPrizesRequest request, CancellationToken ct);
}

public static class AdminValidation
{
    public static void ValidatePage(int page, int pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1) errors["page"] = ["Page must be greater than zero."];
        if (pageSize is < 1 or > 100) errors["pageSize"] = ["PageSize must be between 1 and 100."];
        if (errors.Count > 0) ValidationResult.Failure(errors).ThrowIfInvalid();
    }

    public static byte[] RowVersion(string? value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value ?? string.Empty);
            if (bytes.Length == 8) return bytes;
        }
        catch (FormatException) { }
        ValidationResult.Failure("rowVersion", "RowVersion must be a valid base64 SQL rowversion.").ThrowIfInvalid();
        return [];
    }
}
