using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LuckyWheel.Infrastructure.Persistence.Configurations;

public class WheelVersionConfiguration : IEntityTypeConfiguration<WheelVersion>
{
    public void Configure(EntityTypeBuilder<WheelVersion> builder)
    {
        builder.ToTable("WheelVersions");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.WheelId)
            .IsRequired();

        builder.Property(x => x.VersionNumber)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.StartAtUtc)
            .IsRequired();

        builder.Property(x => x.EndAtUtc)
            .IsRequired();

        builder.Property(x => x.ClaimDurationMinutes)
            .IsRequired();

        builder.Property<byte[]>("RowVersion")
            .IsRowVersion()
            .IsRequired()
            .IsConcurrencyToken();

        builder.HasOne<Wheel>()
            .WithMany()
            .HasForeignKey(x => x.WheelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.WheelId, x.VersionNumber })
            .IsUnique();

        builder.HasIndex(x => x.WheelId)
            .IsUnique()
            .HasFilter("[Status] = 'Active'");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_WheelVersions_ValidPeriod", "[EndAtUtc] > [StartAtUtc]");
            t.HasCheckConstraint("CK_WheelVersions_ClaimDuration", "[ClaimDurationMinutes] > 0");
            t.HasCheckConstraint("CK_WheelVersions_VersionNumber", "[VersionNumber] > 0");
        });
    }
}
