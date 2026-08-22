using ProjectManagementWeb.Domain.Common;
using ProjectManagementWeb.Domain.Enums;

namespace ProjectManagementWeb.Domain.Entities;

public sealed class Project : AuditableEntity
{
    private Project() { }

    public Project(Guid id, string code, string name, string? description, Guid ownerAccountId, DateTimeOffset now)
    {
        Id = id;
        Code = code;
        Name = name;
        Description = description;
        OwnerAccountId = ownerAccountId;
        Status = ProjectStatus.Pending;
        MarkCreated(now);
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid OwnerAccountId { get; private set; }
    public ProjectStatus Status { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public void Update(string name, string? description, Guid ownerAccountId, ProjectStatus status, DateTimeOffset now)
    {
        Name = name;
        Description = description;
        OwnerAccountId = ownerAccountId;
        Status = status;
        MarkUpdated(now);
    }

    public void SoftDelete(DateTimeOffset now)
    {
        DeletedAt = now;
        MarkUpdated(now);
    }
}
