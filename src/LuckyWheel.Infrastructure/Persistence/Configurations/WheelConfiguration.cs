using LuckyWheel.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LuckyWheel.Infrastructure.Persistence.Configurations;

public class WheelConfiguration : IEntityTypeConfiguration<Wheel>
{
    public void Configure(EntityTypeBuilder<Wheel> builder)
    {
        builder.ToTable("Wheels");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.Slug)
            .HasMaxLength(200)
            .IsRequired();

        builder.HasIndex(x => x.Slug)
            .IsUnique();

        builder.Property(x => x.Description)
            .HasMaxLength(1000);

        builder.Property(x => x.Terms)
            .IsRequired();

        builder.Property(x => x.IsEnabled)
            .IsRequired();

        builder.Property(x => x.CreatedAtUtc)
            .IsRequired();
    }
}
