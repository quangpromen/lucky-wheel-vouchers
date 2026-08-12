using System;
using LuckyWheel.Domain.Common;
using LuckyWheel.Domain.Enums;

namespace LuckyWheel.Domain.Entities;

public class AuditLog : Entity
{
    public Guid? AdminUserId { get; private set; }
    public AuditAction Action { get; private set; }
    public string EntityType { get; private set; }
    public Guid? EntityId { get; private set; }
    public string Description { get; private set; }
    public string? MetadataJson { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public AuditLog(
        Guid? adminUserId,
        AuditAction action,
        string entityType,
        Guid? entityId,
        string description,
        string? metadataJson,
        DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            throw new DomainException("AUDIT_ENTITY_TYPE_REQUIRED", "EntityType is required.");
        if (string.IsNullOrWhiteSpace(description))
            throw new DomainException("AUDIT_DESCRIPTION_REQUIRED", "Description is required.");

        AdminUserId = adminUserId;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        Description = description;
        MetadataJson = metadataJson;
        CreatedAtUtc = createdAtUtc;
    }
}
