using LuckyWheel.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LuckyWheel.Infrastructure.Persistence.Configurations;

public class WheelVersionPrizeConfiguration : IEntityTypeConfiguration<WheelVersionPrize>
{
    public void Configure(EntityTypeBuilder<WheelVersionPrize> builder)
    {
        builder.ToTable("WheelVersionPrizes");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.WheelVersionId)
            .IsRequired();

        builder.Property(x => x.ProbabilityWeight)
            .IsRequired();

        builder.Property(x => x.DisplayOrder)
            .IsRequired();

        builder.Property(x => x.Color)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.ImageUrl)
            .HasMaxLength(2048);

        builder.Property(x => x.IsNoPrize)
            .IsRequired();

        builder.HasOne<WheelVersion>()
            .WithMany()
            .HasForeignKey(x => x.WheelVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Prize>()
            .WithMany()
            .HasForeignKey(x => x.PrizeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.WheelVersionId, x.DisplayOrder })
            .IsUnique();

        builder.HasIndex(x => new { x.WheelVersionId, x.PrizeId });

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_WheelVersionPrizes_ProbabilityWeight", "[ProbabilityWeight] >= 0");
            t.HasCheckConstraint("CK_WheelVersionPrizes_DisplayOrder", "[DisplayOrder] > 0");
            t.HasCheckConstraint("CK_WheelVersionPrizes_PrizeReference", "([IsNoPrize] = 1 AND [PrizeId] IS NULL) OR ([IsNoPrize] = 0 AND [PrizeId] IS NOT NULL)");
        });
    }
}
