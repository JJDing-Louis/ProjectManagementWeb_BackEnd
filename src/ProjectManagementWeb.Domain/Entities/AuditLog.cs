namespace ProjectManagementWeb.Domain.Entities;

public sealed class AuditLog
{
    private AuditLog() { }

    public AuditLog(Guid id, Guid? actorAccountId, string action, string entityType, string entityId,
        string? beforeData, string? afterData, DateTimeOffset createdAt)
    {
        Id = id;
        ActorAccountId = actorAccountId;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        BeforeData = beforeData;
        AfterData = afterData;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid? ActorAccountId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public string EntityId { get; private set; } = string.Empty;
    public string? BeforeData { get; private set; }
    public string? AfterData { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
