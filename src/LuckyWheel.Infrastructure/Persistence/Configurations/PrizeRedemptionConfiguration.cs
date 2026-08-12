using LuckyWheel.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LuckyWheel.Infrastructure.Persistence.Configurations;

public class PrizeRedemptionConfiguration : IEntityTypeConfiguration<PrizeRedemption>
{
    public void Configure(EntityTypeBuilder<PrizeRedemption> builder)
    {
        builder.ToTable("PrizeRedemptions");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.SpinId)
            .IsRequired();

        builder.Property(x => x.PrizeKeyId)
            .IsRequired();

        builder.Property(x => x.ConfirmedByAdminId)
            .IsRequired();

        builder.Property(x => x.Note)
            .HasMaxLength(1000);

        builder.HasOne<SpinHistory>()
            .WithMany()
            .HasForeignKey(x => x.SpinId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PrizeKey>()
            .WithMany()
            .HasForeignKey(x => x.PrizeKeyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AdminUser>()
            .WithMany()
            .HasForeignKey(x => x.ConfirmedByAdminId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.SpinId)
            .IsUnique();

        builder.HasIndex(x => x.PrizeKeyId)
            .IsUnique();
    }
}
