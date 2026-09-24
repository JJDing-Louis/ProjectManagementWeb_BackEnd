using ProjectManagementWeb.Domain.Common;

namespace ProjectManagementWeb.Domain.Entities;

public sealed class TaskItemComment : AuditableEntity
{
    private TaskItemComment() { }

    public TaskItemComment(Guid id, Guid taskItemId, Guid authorAccountId, string content, DateTimeOffset now)
    {
        Id = id;
        TaskItemId = taskItemId;
        AuthorAccountId = authorAccountId;
        Content = content;
        MarkCreated(now);
    }

    public Guid Id { get; private set; }
    public Guid TaskItemId { get; private set; }
    public Guid AuthorAccountId { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public DateTimeOffset? DeletedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public void Update(string content, DateTimeOffset now)
    {
        Content = content;
        MarkUpdated(now);
    }

    public void SoftDelete(DateTimeOffset now)
    {
        DeletedAt = now;
        MarkUpdated(now);
    }
}
