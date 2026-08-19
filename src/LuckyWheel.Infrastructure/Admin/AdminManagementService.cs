using System.Data;
using System.Linq;
using LuckyWheel.Application.Common.Authentication;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Application.Common.Validation;
using LuckyWheel.Application.Features.Admin;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using LuckyWheel.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LuckyWheel.Infrastructure.Admin;

public sealed class AdminManagementService(ApplicationDbContext db, IClock clock, ICurrentAdminContext adminContext) : IAdminManagementService
{
    public async Task<WheelDto> CreateWheelAsync(CreateWheelRequest r, CancellationToken ct)
    {
        ValidateWheel(r.Name, r.Slug, r.Terms);
        var slug = r.Slug!.Trim();
        if (await db.Wheels.AnyAsync(x => x.Slug == slug, ct)) throw new ConflictException("Wheel slug already exists.");
        var entity = new Wheel(r.Name!, slug, r.Description, r.Terms!, Now);
        db.Wheels.Add(entity); Audit(AuditAction.Created, "Wheel", entity.Id, "Wheel created.");
        await SaveAsync("Wheel slug already exists.", ct);
        return Wheel(entity);
    }

    public async Task<WheelDto> GetWheelAsync(Guid id, CancellationToken ct) =>
        Wheel(await db.Wheels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("Wheel", id.ToString()));

    public async Task<PageResult<WheelDto>> GetWheelsAsync(int page, int size, CancellationToken ct)
    {
        AdminValidation.ValidatePage(page, size);
        var query = db.Wheels.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc);
        var total = await query.CountAsync(ct);
        return new((await query.Skip((page - 1) * size).Take(size).ToListAsync(ct)).Select(Wheel).ToList(), page, size, total);
    }

    public async Task<WheelDto> UpdateWheelAsync(Guid id, UpdateWheelRequest r, CancellationToken ct)
    {
        ValidateWheel(r.Name, r.Slug, r.Terms);
        var entity = await db.Wheels.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Wheel", id.ToString());
        var slug = r.Slug!.Trim();
        if (await db.Wheels.AnyAsync(x => x.Slug == slug && x.Id != id, ct)) throw new ConflictException("Wheel slug already exists.");
        entity.Update(r.Name!, slug, r.Description, r.Terms!, Now); Audit(AuditAction.Updated, "Wheel", entity.Id, "Wheel updated.");
        await SaveAsync("Wheel slug already exists.", ct);
        return Wheel(entity);
    }

    public async Task<PrizeDto> CreatePrizeAsync(CreatePrizeRequest r, CancellationToken ct)
    {
        ValidatePrize(r.Name, r.Description, r.ImageUrl, r.RequiresKey, r.TotalQuantity, false, null);
        if (!await db.Wheels.AnyAsync(x => x.Id == r.WheelId, ct)) throw new NotFoundException("Wheel", r.WheelId.ToString());
        var entity = new Prize(r.WheelId, r.Name!, r.Description, r.ImageUrl, r.RequiresKey, r.TotalQuantity, Now);
        db.Prizes.Add(entity); Audit(AuditAction.Created, "Prize", entity.Id, "Prize created."); await SaveAsync("Prize could not be created.", ct);
        return Prize(entity);
    }

    public async Task<PrizeDto> GetPrizeAsync(Guid id, CancellationToken ct) => Prize(
        await db.Prizes.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Prize", id.ToString()));

    public async Task<PageResult<PrizeDto>> GetPrizesAsync(int page, int size, bool? requiresKey, Guid? wheelId, CancellationToken ct)
    {
        AdminValidation.ValidatePage(page, size);
        var query = db.Prizes.AsQueryable();
        if (requiresKey.HasValue) query = query.Where(x => x.RequiresKey == requiresKey.Value);
        if (wheelId.HasValue) query = query.Where(x => x.WheelId == wheelId.Value);
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Name).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new(items.Select(Prize).ToList(), page, size, total);
    }

    public async Task<PrizeDto> UpdatePrizeAsync(Guid id, UpdatePrizeRequest r, CancellationToken ct)
    {
        ValidatePrize(r.Name, r.Description, r.ImageUrl, r.RequiresKey, r.TotalQuantity, true, r.RowVersion);
        var entity = await db.Prizes.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Prize", id.ToString());
        db.Entry(entity).Property<byte[]>("RowVersion").OriginalValue = AdminValidation.RowVersion(r.RowVersion);
        var used = await db.WheelVersionPrizes.AnyAsync(x => x.PrizeId == id, ct) || await db.SpinHistories.AnyAsync(x => x.PrizeId == id, ct);
        if (used && (entity.RequiresKey != r.RequiresKey || r.TotalQuantity < entity.TotalQuantity))
            throw new ConflictException("A used prize cannot change key requirements or reduce total quantity.");
        entity.Update(r.Name!, r.Description, r.ImageUrl, r.RequiresKey, r.TotalQuantity, Now); Audit(AuditAction.Updated, "Prize", entity.Id, "Prize updated.");
        await SaveAsync("Prize was changed by another admin.", ct); return Prize(entity);
    }

    public async Task<WheelVersionDto> CreateDraftVersionAsync(Guid wheelId, CreateDraftWheelVersionRequest r, CancellationToken ct)
    {
        ValidateVersion(r.StartAtUtc, r.EndAtUtc, r.ClaimDurationMinutes, false, null);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await db.Wheels.AnyAsync(x => x.Id == wheelId, ct)) throw new NotFoundException("Wheel", wheelId.ToString());
            var number = (await db.WheelVersions.Where(x => x.WheelId == wheelId).MaxAsync(x => (int?)x.VersionNumber, ct) ?? 0) + 1;
            var entity = new WheelVersion(wheelId, number, r.StartAtUtc, r.EndAtUtc, r.ClaimDurationMinutes, Now);
            db.WheelVersions.Add(entity); Audit(AuditAction.Created, "WheelVersion", entity.Id, "Draft wheel version created.");
            await SaveAsync("A wheel version was created concurrently. Retry the request.", ct);
            await tx.CommitAsync(ct); return Version(entity, []);
        });
    }

    public async Task<WheelVersionDto> GetVersionAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.WheelVersions.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("WheelVersion", id.ToString());
        var prizes = await db.WheelVersionPrizes.Where(x => x.WheelVersionId == id).OrderBy(x => x.DisplayOrder).ToListAsync(ct);
        return Version(entity, prizes);
    }

    public async Task<PageResult<WheelVersionDto>> GetVersionsAsync(Guid wheelId, int page, int size, CancellationToken ct)
    {
        AdminValidation.ValidatePage(page, size);
        if (!await db.Wheels.AnyAsync(x => x.Id == wheelId, ct)) throw new NotFoundException("Wheel", wheelId.ToString());
        var query = db.WheelVersions.Where(x => x.WheelId == wheelId).OrderByDescending(x => x.VersionNumber);
        var total = await query.CountAsync(ct); var entities = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var ids = entities.Select(x => x.Id).ToList();
        var prizes = await db.WheelVersionPrizes.Where(x => ids.Contains(x.WheelVersionId)).OrderBy(x => x.DisplayOrder).ToListAsync(ct);
        return new(entities.Select(x => Version(x, prizes.Where(p => p.WheelVersionId == x.Id))).ToList(), page, size, total);
    }

    public async Task<WheelVersionDto> UpdateDraftVersionAsync(Guid id, UpdateDraftWheelVersionRequest r, CancellationToken ct)
    {
        ValidateVersion(r.StartAtUtc, r.EndAtUtc, r.ClaimDurationMinutes, true, r.RowVersion);
        var entity = await Draft(id, ct); db.Entry(entity).Property<byte[]>("RowVersion").OriginalValue = AdminValidation.RowVersion(r.RowVersion);
        entity.UpdateSchedule(r.StartAtUtc, r.EndAtUtc, r.ClaimDurationMinutes, Now); Audit(AuditAction.Updated, "WheelVersion", entity.Id, "Draft wheel version updated.");
        await SaveAsync("Wheel version was changed by another admin.", ct);
        var prizes = await db.WheelVersionPrizes.Where(x => x.WheelVersionId == id).OrderBy(x => x.DisplayOrder).ToListAsync(ct);
        return Version(entity, prizes);
    }

    public async Task<WheelVersionDto> ActivateVersionAsync(Guid versionId, ActivateDraftWheelVersionRequest r, CancellationToken ct)
    {
        var rowVersion = AdminValidation.RowVersion(r.RowVersion);
        var adminId = adminContext.AdminId ?? Guid.Empty;
        if (adminId == Guid.Empty)
            throw new BusinessRuleViolationException("WHEEL_VERSION_INVALID_PUBLISHER", "An authenticated admin identity is required to activate a version.");

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var version = await db.WheelVersions.SingleOrDefaultAsync(x => x.Id == versionId, ct)
                ?? throw new NotFoundException("WheelVersion", versionId.ToString());

            if (version.Status != WheelVersionStatus.Draft)
                throw new ConflictException("Only Draft wheel versions can be activated.");

            var segments = await db.WheelVersionPrizes.Where(x => x.WheelVersionId == versionId).OrderBy(x => x.DisplayOrder).ToListAsync(ct);
            if (segments.Count == 0)
                throw new BusinessRuleViolationException("WHEEL_VERSION_NO_SEGMENTS", "Wheel version must have at least one prize segment before activation.");
            if (segments.Any(x => x.ProbabilityWeight <= 0) || segments.Sum(x => (long)x.ProbabilityWeight) != 1_000_000)
                throw new BusinessRuleViolationException("WHEEL_VERSION_INVALID_WEIGHT", "All segment weights must be positive and total exactly 1,000,000.");
            if (segments.Count(x => x.IsNoPrize) != 1)
                throw new BusinessRuleViolationException("WHEEL_VERSION_INVALID_NO_PRIZE", "Wheel version must contain exactly one NoPrize segment.");

            var orders = segments.Select(x => x.DisplayOrder).ToList();
            if (orders.Distinct().Count() != segments.Count || !orders.SequenceEqual(Enumerable.Range(1, segments.Count)))
                throw new BusinessRuleViolationException("WHEEL_VERSION_INVALID_DISPLAY_ORDER", "Segment display orders must be unique and sequential from 1 to the number of segments.");
            if (segments.Any(x => x.IsNoPrize && x.PrizeId.HasValue))
                throw new BusinessRuleViolationException("WVP_PRIZE_ID_MUST_BE_NULL", "PrizeId must be null for NoPrize segments.");
            if (segments.Any(x => !x.IsNoPrize && !x.PrizeId.HasValue))
                throw new BusinessRuleViolationException("WVP_PRIZE_ID_REQUIRED", "PrizeId is required for prize segments.");

            var prizeIds = segments.Where(x => !x.IsNoPrize).Select(x => x.PrizeId!.Value).Distinct().ToList();
            var prizes = await db.Prizes.Where(x => prizeIds.Contains(x.Id)).ToListAsync(ct);
            if (prizes.Count != prizeIds.Count || prizes.Any(x => x.WheelId != version.WheelId || !x.IsEnabled))
                throw new BusinessRuleViolationException("WHEEL_VERSION_INVALID_PRIZES", "Referenced prizes must exist, be enabled, and belong to the same wheel.");
            foreach (var prize in prizes.Where(x => x.RequiresKey))
                if (!await db.PrizeKeys.AnyAsync(x => x.PrizeId == prize.Id && x.Status == PrizeKeyStatus.Available, ct))
                    throw new BusinessRuleViolationException("PRIZE_KEY_NOT_AVAILABLE", $"Prize '{prize.Name}' requires at least one Available prize key before version activation.");

            if (await db.WheelVersions.AnyAsync(x => x.WheelId == version.WheelId && x.Status == WheelVersionStatus.Active && x.Id != versionId, ct))
                throw new ConflictException("Another wheel version is currently active for this wheel. Close the active version before activating a new one.");

            db.Entry(version).Property<byte[]>("RowVersion").OriginalValue = rowVersion;
            version.Activate(adminId, Now);
            Audit(AuditAction.Activated, "WheelVersion", version.Id, $"Wheel version {version.VersionNumber} activated.");
            await SaveAsync("Wheel version was changed by another admin.", ct);
            await tx.CommitAsync(ct);
            return Version(version, segments);
        });
    }

    public async Task<WheelVersionDto> CloseVersionAsync(Guid versionId, CloseActiveWheelVersionRequest r, CancellationToken ct)
    {
        var rowVersion = AdminValidation.RowVersion(r.RowVersion);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var version = await db.WheelVersions.SingleOrDefaultAsync(x => x.Id == versionId, ct)
                ?? throw new NotFoundException("WheelVersion", versionId.ToString());

            if (version.Status != WheelVersionStatus.Active)
                throw new ConflictException("Only Active wheel versions can be closed.");

            db.Entry(version).Property<byte[]>("RowVersion").OriginalValue = rowVersion;
            version.Close(Now);
            Audit(AuditAction.Closed, "WheelVersion", version.Id, $"Wheel version {version.VersionNumber} closed.");
            await SaveAsync("Wheel version was changed by another admin.", ct);
            await tx.CommitAsync(ct);
            var segments = await db.WheelVersionPrizes.Where(x => x.WheelVersionId == versionId).OrderBy(x => x.DisplayOrder).ToListAsync(ct);
            return Version(version, segments);
        });
    }

    public async Task<WheelVersionPrizeDto> AddVersionPrizeAsync(Guid versionId, CreateWheelVersionPrizeRequest r, CancellationToken ct)
    {
        ValidateSegment(r.PrizeId, r.IsNoPrize, r.Weight, r.DisplayOrder, r.Color, false, null);
        var version = await Draft(versionId, ct);
        if (r.PrizeId.HasValue && !await db.Prizes.AnyAsync(x => x.Id == r.PrizeId && x.WheelId == version.WheelId && x.IsEnabled, ct))
            throw new NotFoundException("Prize", r.PrizeId.Value.ToString());
        if (await db.WheelVersionPrizes.AnyAsync(x => x.WheelVersionId == versionId && x.DisplayOrder == r.DisplayOrder, ct)) throw new ConflictException("DisplayOrder already exists.");
        var entity = new WheelVersionPrize(versionId, r.PrizeId, r.Weight, r.DisplayOrder, r.Color!, r.ImageUrl, r.IsNoPrize, Now);
        db.WheelVersionPrizes.Add(entity); Audit(AuditAction.Created, "WheelVersionPrize", entity.Id, "Draft wheel segment created."); await SaveAsync("DisplayOrder already exists.", ct); return Segment(entity);
    }

    public async Task<WheelVersionPrizeDto> UpdateVersionPrizeAsync(Guid versionId, Guid id, UpdateWheelVersionPrizeRequest r, CancellationToken ct)
    {
        ValidateSegment(null, true, r.Weight, r.DisplayOrder, r.Color, true, r.RowVersion); await Draft(versionId, ct);
        var entity = await db.WheelVersionPrizes.SingleOrDefaultAsync(x => x.Id == id && x.WheelVersionId == versionId, ct) ?? throw new NotFoundException("WheelVersionPrize", id.ToString());
        db.Entry(entity).Property<byte[]>("RowVersion").OriginalValue = AdminValidation.RowVersion(r.RowVersion);
        if (await db.WheelVersionPrizes.AnyAsync(x => x.WheelVersionId == versionId && x.DisplayOrder == r.DisplayOrder && x.Id != id, ct)) throw new ConflictException("DisplayOrder already exists.");
        entity.UpdateConfiguration(r.Weight, r.DisplayOrder, r.Color!, r.ImageUrl, Now); Audit(AuditAction.Updated, "WheelVersionPrize", entity.Id, "Draft wheel segment updated.");
        await SaveAsync("Wheel segment was changed by another admin.", ct); return Segment(entity);
    }

    public async Task DeleteVersionPrizeAsync(Guid versionId, Guid id, string rowVersion, CancellationToken ct)
    {
        await Draft(versionId, ct); var token = AdminValidation.RowVersion(rowVersion);
        var entity = await db.WheelVersionPrizes.SingleOrDefaultAsync(x => x.Id == id && x.WheelVersionId == versionId, ct) ?? throw new NotFoundException("WheelVersionPrize", id.ToString());
        db.Entry(entity).Property<byte[]>("RowVersion").OriginalValue = token; db.Remove(entity); Audit(AuditAction.Updated, "WheelVersionPrize", entity.Id, "Draft wheel segment deleted.");
        await SaveAsync("Wheel segment was changed by another admin.", ct);
    }

    public async Task<IReadOnlyList<WheelVersionPrizeDto>> ReorderVersionPrizesAsync(Guid versionId, ReorderWheelVersionPrizesRequest r, CancellationToken ct)
    {
        await Draft(versionId, ct);
        if (r.Items is null || r.Items.Count == 0 || r.Items.Any(x => x.DisplayOrder <= 0) || r.Items.Select(x => x.Id).Distinct().Count() != r.Items.Count || r.Items.Select(x => x.DisplayOrder).Distinct().Count() != r.Items.Count)
            ValidationResult.Failure("items", "Items must be non-empty with unique ids and positive unique display orders.").ThrowIfInvalid();
        var items = r.Items!;
        var ids = items.Select(x => x.Id).ToList(); var entities = await db.WheelVersionPrizes.Where(x => x.WheelVersionId == versionId).ToListAsync(ct);
        if (entities.Count != ids.Count || entities.Any(x => !ids.Contains(x.Id))) ValidationResult.Failure("items", "Reorder must include every segment exactly once.").ThrowIfInvalid();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var offset = entities.Max(x => x.DisplayOrder) + items.Max(x => x.DisplayOrder) + 1;
            foreach (var item in items) { var entity = entities.Single(x => x.Id == item.Id); db.Entry(entity).Property<byte[]>("RowVersion").OriginalValue = AdminValidation.RowVersion(item.RowVersion); entity.UpdateConfiguration(entity.ProbabilityWeight, offset + item.DisplayOrder, entity.Color, entity.ImageUrl, Now); }
            await SaveAsync("Wheel segments were changed by another admin.", ct);
            foreach (var item in items) entities.Single(x => x.Id == item.Id).UpdateConfiguration(entities.Single(x => x.Id == item.Id).ProbabilityWeight, item.DisplayOrder, entities.Single(x => x.Id == item.Id).Color, entities.Single(x => x.Id == item.Id).ImageUrl, Now);
            Audit(AuditAction.Updated, "WheelVersion", versionId, "Draft wheel segments reordered.");
            await SaveAsync("Wheel segments were changed by another admin.", ct); await tx.CommitAsync(ct);
        });
        return entities.OrderBy(x => x.DisplayOrder).Select(Segment).ToList();
    }

    private DateTime Now => clock.UtcNow.UtcDateTime;
    private void Audit(AuditAction action, string type, Guid id, string description)
    {
        var adminId = adminContext.AdminId;
        db.AuditLogs.Add(new AuditLog(adminId, action, type, id, description, null, Now));
    }
    private async Task<WheelVersion> Draft(Guid id, CancellationToken ct)
    {
        var entity = await db.WheelVersions.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("WheelVersion", id.ToString());
        if (entity.Status != WheelVersionStatus.Draft) throw new ConflictException("Only Draft wheel versions can be changed."); return entity;
    }
    private async Task SaveAsync(string conflict, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException(conflict); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { throw new ConflictException(conflict); }
    }
    private string Token<TEntity>(TEntity entity) where TEntity : class => Convert.ToBase64String(db.Entry(entity).Property<byte[]>("RowVersion").CurrentValue ?? []);
    private static WheelDto Wheel(Wheel x) => new(x.Id, x.Name, x.Slug, x.Description, x.Terms, x.IsEnabled, x.CreatedAtUtc, x.UpdatedAtUtc);
    private PrizeDto Prize(Prize x) => new(x.Id, x.WheelId, x.Name, x.Description, x.ImageUrl, x.RequiresKey, x.TotalQuantity, x.IsEnabled, Token(x), x.CreatedAtUtc, x.UpdatedAtUtc);
    private WheelVersionPrizeDto Segment(WheelVersionPrize x) => new(x.Id, x.WheelVersionId, x.PrizeId, x.IsNoPrize, x.ProbabilityWeight, x.DisplayOrder, x.Color, x.ImageUrl, Token(x));
    private WheelVersionDto Version(WheelVersion x, IEnumerable<WheelVersionPrize> p) => new(x.Id, x.WheelId, x.VersionNumber, x.Status.ToString(), x.StartAtUtc, x.EndAtUtc, x.ClaimDurationMinutes, Token(x), p.Select(Segment).ToList(), x.CreatedAtUtc, x.UpdatedAtUtc);

    private static void ValidateWheel(string? name, string? slug, string? terms)
    { var e = new Dictionary<string,string[]>(); if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) e["name"]=["Name is required and must not exceed 200 characters."]; if (string.IsNullOrWhiteSpace(slug) || slug.Trim().Length > 200) e["slug"]=["Slug is required and must not exceed 200 characters."]; if (string.IsNullOrWhiteSpace(terms)) e["terms"]=["Terms is required."]; if(e.Count>0) ValidationResult.Failure(e).ThrowIfInvalid(); }
    private static void ValidatePrize(string? name, string? description, string? imageUrl, bool requiresKey, int qty, bool update, string? rv)
    { var e=new Dictionary<string,string[]>(); if(string.IsNullOrWhiteSpace(name)||name.Trim().Length>200)e["name"]=["Name is required and must not exceed 200 characters."]; if(description?.Trim().Length>1000)e["description"]=["Description must not exceed 1000 characters."]; if(imageUrl?.Trim().Length>2048)e["imageUrl"]=["ImageUrl must not exceed 2048 characters."]; if(qty<0||requiresKey&&qty<=0)e["totalQuantity"]=["Quantity must be non-negative and greater than zero when RequiresKey is true."]; if(update&&string.IsNullOrWhiteSpace(rv))e["rowVersion"]=["RowVersion is required."]; if(e.Count>0)ValidationResult.Failure(e).ThrowIfInvalid(); }
    private static void ValidateVersion(DateTime start, DateTime end, int duration, bool update, string? rv)
    { var e=new Dictionary<string,string[]>(); if(end<=start)e["endAtUtc"]=["EndAtUtc must be greater than StartAtUtc."]; if(duration<=0)e["claimDurationMinutes"]=["ClaimDurationMinutes must be greater than zero."]; if(update&&string.IsNullOrWhiteSpace(rv))e["rowVersion"]=["RowVersion is required."]; if(e.Count>0)ValidationResult.Failure(e).ThrowIfInvalid(); }
    private static void ValidateSegment(Guid? prizeId,bool noPrize,int weight,int order,string? color,bool update,string? rv)
    { var e=new Dictionary<string,string[]>(); if(!update&&noPrize&&prizeId.HasValue||!update&&!noPrize&&!prizeId.HasValue)e["prizeId"]=["PrizeId must be null only for a no-prize segment."]; if(weight<=0)e["weight"]=["Weight must be greater than zero."]; if(order<=0)e["displayOrder"]=["DisplayOrder must be greater than zero."]; if(string.IsNullOrWhiteSpace(color)||color.Trim().Length>50)e["color"]=["Color is required and must not exceed 50 characters."]; if(update&&string.IsNullOrWhiteSpace(rv))e["rowVersion"]=["RowVersion is required."]; if(e.Count>0)ValidationResult.Failure(e).ThrowIfInvalid(); }
}
