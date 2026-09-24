namespace ProjectManagementWeb.Domain.Entities;

public sealed class ProjectReminderRun
{
    private ProjectReminderRun() { }

    public ProjectReminderRun(Guid id, Guid projectId, DateOnly reminderDate, DateTimeOffset startedAt)
    {
        Id = id;
        ProjectId = projectId;
        ReminderDate = reminderDate;
        StartedAt = startedAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public DateOnly ReminderDate { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public int CreatedCount { get; private set; }
    public int DuplicateCount { get; private set; }
    public int SkippedCount { get; private set; }

    public void Complete(int createdCount, int duplicateCount, int skippedCount, DateTimeOffset completedAt)
    {
        CreatedCount = createdCount;
        DuplicateCount = duplicateCount;
        SkippedCount = skippedCount;
        CompletedAt = completedAt;
    }
}
