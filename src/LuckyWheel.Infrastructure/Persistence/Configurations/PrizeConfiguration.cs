using LuckyWheel.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LuckyWheel.Infrastructure.Persistence.Configurations;

public class PrizeConfiguration : IEntityTypeConfiguration<Prize>
{
    public void Configure(EntityTypeBuilder<Prize> builder)
    {
        builder.ToTable("Prizes");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.WheelId)
            .IsRequired();

        builder.Property(x => x.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.Description)
            .HasMaxLength(1000);

        builder.Property(x => x.ImageUrl)
            .HasMaxLength(2048);

        builder.Property(x => x.RequiresKey)
            .IsRequired();

        builder.Property(x => x.TotalQuantity)
            .IsRequired();

        builder.Property(x => x.IsEnabled)
            .IsRequired();

        builder.Property<byte[]>("RowVersion")
            .IsRowVersion()
            .IsRequired()
            .IsConcurrencyToken();

        builder.HasOne<Wheel>()
            .WithMany()
            .HasForeignKey(x => x.WheelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Prizes_TotalQuantity", "[TotalQuantity] >= 0");
            t.HasCheckConstraint("CK_Prizes_KeyRequiresQuantity", "[RequiresKey] = 0 OR [TotalQuantity] > 0");
        });
    }
}
