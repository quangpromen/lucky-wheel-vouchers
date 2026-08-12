using LuckyWheel.Domain.Entities;
using LuckyWheel.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LuckyWheel.Infrastructure.Persistence.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Action)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.EntityType)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.Description)
            .HasMaxLength(2000)
            .IsRequired();

        builder.Property(x => x.MetadataJson)
            .HasColumnType("nvarchar(max)");

        builder.HasOne<AdminUser>()
            .WithMany()
            .HasForeignKey(x => x.AdminUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.AdminUserId, x.CreatedAtUtc });
        builder.HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAtUtc });
        builder.HasIndex(x => new { x.Action, x.CreatedAtUtc });
    }
}
