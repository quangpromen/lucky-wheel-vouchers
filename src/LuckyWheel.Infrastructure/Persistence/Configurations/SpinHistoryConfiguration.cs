using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LuckyWheel.Infrastructure.Persistence.Configurations;

public class SpinHistoryConfiguration : IEntityTypeConfiguration<SpinHistory>
{
    public void Configure(EntityTypeBuilder<SpinHistory> builder)
    {
        builder.ToTable("SpinHistories");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.WheelId)
            .IsRequired();

        builder.Property(x => x.WheelVersionId)
            .IsRequired();

        builder.Property(x => x.WheelVersionPrizeId)
            .IsRequired();

        builder.Property(x => x.EmailOriginal)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(x => x.EmailNormalized)
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(x => x.Result)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.IdempotencyKey)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.ReceiptToken)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.AcceptedTermsAtUtc)
            .IsRequired();

        builder.Property(x => x.CreatedAtUtc)
            .IsRequired();

        builder.Property(x => x.PrizeNameSnapshot)
            .HasMaxLength(200);

        builder.Property(x => x.PrizeDescriptionSnapshot)
            .HasMaxLength(1000);

        builder.Property(x => x.CancellationReason)
            .HasMaxLength(1000);

        builder.HasOne<Wheel>()
            .WithMany()
            .HasForeignKey(x => x.WheelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<WheelVersion>()
            .WithMany()
            .HasForeignKey(x => x.WheelVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<WheelVersionPrize>()
            .WithMany()
            .HasForeignKey(x => x.WheelVersionPrizeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Prize>()
            .WithMany()
            .HasForeignKey(x => x.PrizeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PrizeKey>()
            .WithMany()
            .HasForeignKey(x => x.PrizeKeyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.IdempotencyKey)
            .IsUnique();

        builder.HasIndex(x => x.ReceiptToken)
            .IsUnique();

        builder.HasIndex(x => x.PrizeKeyId)
            .IsUnique()
            .HasFilter("[PrizeKeyId] IS NOT NULL");

        builder.HasIndex(x => new { x.WheelId, x.EmailNormalized, x.CreatedAtUtc });
        builder.HasIndex(x => new { x.WheelVersionId, x.CreatedAtUtc });
        builder.HasIndex(x => new { x.WheelVersionPrizeId, x.CreatedAtUtc });
        builder.HasIndex(x => new { x.PrizeId, x.CreatedAtUtc });
    }
}
