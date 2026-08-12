using LuckyWheel.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LuckyWheel.Infrastructure.Persistence.Configurations;

public class WinnerLockConfiguration : IEntityTypeConfiguration<WinnerLock>
{
    public void Configure(EntityTypeBuilder<WinnerLock> builder)
    {
        builder.ToTable("WinnerLocks");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.WheelId)
            .IsRequired();

        builder.Property(x => x.EmailNormalized)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(x => x.SpinId)
            .IsRequired();

        builder.Property(x => x.PrizeKeyId)
            .IsRequired();

        builder.Property(x => x.IsActive)
            .IsRequired();

        builder.Property(x => x.IsBlocked)
            .IsRequired();

        builder.Property(x => x.BlockReason)
            .HasMaxLength(1000);

        builder.Property<byte[]>("RowVersion")
            .IsRowVersion()
            .IsRequired()
            .IsConcurrencyToken();

        builder.HasOne<Wheel>()
            .WithMany()
            .HasForeignKey(x => x.WheelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SpinHistory>()
            .WithMany()
            .HasForeignKey(x => x.SpinId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PrizeKey>()
            .WithMany()
            .HasForeignKey(x => x.PrizeKeyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.WheelId, x.EmailNormalized })
            .IsUnique()
            .HasFilter("[IsActive] = 1");

        builder.HasIndex(x => x.SpinId);
        builder.HasIndex(x => x.PrizeKeyId);
    }
}
