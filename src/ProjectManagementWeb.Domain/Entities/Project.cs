using ProjectManagementWeb.Domain.Common;
using ProjectManagementWeb.Domain.Enums;

namespace ProjectManagementWeb.Domain.Entities;

public sealed class Project : AuditableEntity
{
    private Project() { }

    public Project(
        Guid id,
        string code,
        string name,
        string? description,
        Guid ownerAccountId,
        string timeZoneId,
        DateTimeOffset now)
    {
        Id = id;
        Code = code;
        Name = name;
        Description = description;
        OwnerAccountId = ownerAccountId;
        TimeZoneId = timeZoneId;
        Status = ProjectStatus.Pending;
        MarkCreated(now);
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid OwnerAccountId { get; private set; }
    public string TimeZoneId { get; private set; } = string.Empty;
    public ProjectStatus Status { get; private set; }
    public int VersionNumber { get; private set; } = 1;
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedByAccountId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public void Update(
        string name,
        string? description,
        Guid ownerAccountId,
        string timeZoneId,
        ProjectStatus status,
        DateTimeOffset now)
    {
        Name = name;
        Description = description;
        OwnerAccountId = ownerAccountId;
        TimeZoneId = timeZoneId;
        Status = status;
        VersionNumber++;
        MarkUpdated(now);
    }

    public void SoftDelete(Guid deletedByAccountId, DateTimeOffset now)
    {
        DeletedAt = now;
        DeletedByAccountId = deletedByAccountId;
        MarkUpdated(now);
    }
}
