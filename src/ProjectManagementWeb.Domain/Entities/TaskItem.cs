using ProjectManagementWeb.Domain.Common;
using TaskStatus = ProjectManagementWeb.Domain.Enums.TaskStatus;

namespace ProjectManagementWeb.Domain.Entities;

public sealed class TaskItem : AuditableEntity
{
    private TaskItem() { }

    public TaskItem(Guid id, string code, Guid projectId, Guid createdByAccountId, Guid assignedAccountId,
        string title, string? description, DateTimeOffset startAt, DateTimeOffset deadline, DateTimeOffset now)
    {
        Id = id;
        Code = code;
        ProjectId = projectId;
        CreatedByAccountId = createdByAccountId;
        AssignedAccountId = assignedAccountId;
        Title = title;
        Description = description;
        StartAt = startAt;
        Deadline = deadline;
        Status = TaskStatus.Pending;
        MarkCreated(now);
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public Guid ProjectId { get; private set; }
    public Guid CreatedByAccountId { get; private set; }
    public Guid AssignedAccountId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DateTimeOffset StartAt { get; private set; }
    public DateTimeOffset Deadline { get; private set; }
    public TaskStatus Status { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public void Update(string title, string? description, Guid assignedAccountId, DateTimeOffset startAt,
        DateTimeOffset deadline, TaskStatus status, DateTimeOffset now)
    {
        Title = title;
        Description = description;
        AssignedAccountId = assignedAccountId;
        StartAt = startAt;
        Deadline = deadline;
        Status = status;
        MarkUpdated(now);
    }

    public void UpdateStatusAndDeadline(TaskStatus status, DateTimeOffset deadline, DateTimeOffset now)
    {
        Status = status;
        Deadline = deadline;
        MarkUpdated(now);
    }

    public void UpdateStatus(TaskStatus status, DateTimeOffset now)
    {
        Status = status;
        MarkUpdated(now);
    }

    public void SoftDelete(DateTimeOffset now)
    {
        DeletedAt = now;
        MarkUpdated(now);
    }
}
