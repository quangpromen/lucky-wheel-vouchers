using LuckyWheel.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LuckyWheel.Infrastructure.Persistence;

public sealed class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Wheel> Wheels => Set<Wheel>();
    public DbSet<WheelVersion> WheelVersions => Set<WheelVersion>();
    public DbSet<Prize> Prizes => Set<Prize>();
    public DbSet<WheelVersionPrize> WheelVersionPrizes => Set<WheelVersionPrize>();
    public DbSet<PrizeKey> PrizeKeys => Set<PrizeKey>();
    public DbSet<SpinHistory> SpinHistories => Set<SpinHistory>();
    public DbSet<WinnerLock> WinnerLocks => Set<WinnerLock>();
    public DbSet<PrizeRedemption> PrizeRedemptions => Set<PrizeRedemption>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
