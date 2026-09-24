namespace ProjectManagementWeb.Domain.Common;

public abstract class AuditableEntity
{
    public DateTimeOffset CreatedAt { get; protected set; }
    public DateTimeOffset UpdatedAt { get; protected set; }

    protected void MarkCreated(DateTimeOffset now)
    {
        CreatedAt = now;
        UpdatedAt = now;
    }

    protected void MarkUpdated(DateTimeOffset now) => UpdatedAt = now;
}
