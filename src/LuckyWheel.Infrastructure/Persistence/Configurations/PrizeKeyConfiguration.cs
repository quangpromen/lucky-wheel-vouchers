using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LuckyWheel.Infrastructure.Persistence.Configurations;

public class PrizeKeyConfiguration : IEntityTypeConfiguration<PrizeKey>
{
    public void Configure(EntityTypeBuilder<PrizeKey> builder)
    {
        builder.ToTable("PrizeKeys");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.PrizeId)
            .IsRequired();

        builder.Property(x => x.CodeHash)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(x => x.CodeEncrypted)
            .HasMaxLength(1000)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property<byte[]>("RowVersion")
            .IsRowVersion()
            .IsRequired()
            .IsConcurrencyToken();

        builder.HasOne<Prize>()
            .WithMany()
            .HasForeignKey(x => x.PrizeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SpinHistory>()
            .WithMany()
            .HasForeignKey(x => x.AssignedSpinId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.CodeHash)
            .IsUnique();

        builder.HasIndex(x => new { x.PrizeId, x.Status, x.CreatedAtUtc });

        builder.HasIndex(x => new { x.Status, x.ExpiresAtUtc });
    }
}
