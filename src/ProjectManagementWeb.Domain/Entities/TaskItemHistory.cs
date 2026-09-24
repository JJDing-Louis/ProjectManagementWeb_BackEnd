namespace ProjectManagementWeb.Domain.Entities;

public sealed class TaskItemHistory
{
    private TaskItemHistory() { }

    public TaskItemHistory(Guid id, Guid taskItemId, Guid actorAccountId, string action, string snapshot, DateTimeOffset createdAt)
    {
        Id = id;
        TaskItemId = taskItemId;
        ActorAccountId = actorAccountId;
        Action = action;
        Snapshot = snapshot;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid TaskItemId { get; private set; }
    public Guid ActorAccountId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string Snapshot { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
}
