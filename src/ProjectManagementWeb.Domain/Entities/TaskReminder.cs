using ProjectManagementWeb.Domain.Enums;

namespace ProjectManagementWeb.Domain.Entities;

public sealed class TaskReminder
{
    private TaskReminder() { }

    public TaskReminder(
        Guid id,
        Guid projectId,
        Guid taskItemId,
        Guid recipientAccountId,
        DateOnly reminderDate,
        DateTimeOffset createdAt)
    {
        Id = id;
        ProjectId = projectId;
        TaskItemId = taskItemId;
        RecipientAccountId = recipientAccountId;
        ReminderDate = reminderDate;
        CreatedAt = createdAt;
        NextAttemptAt = createdAt;
        Status = TaskReminderStatus.Pending;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid TaskItemId { get; private set; }
    public Guid RecipientAccountId { get; private set; }
    public DateOnly ReminderDate { get; private set; }
    public TaskReminderStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public int RetryCount { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? ClaimedAt { get; private set; }
    public Guid? ClaimToken { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public string? ProviderResponseId { get; private set; }
    public string? LastError { get; private set; }
    public string? CancellationReason { get; private set; }
    public DateTimeOffset? AlertedAt { get; private set; }

    public void Claim(Guid claimToken, DateTimeOffset now)
    {
        Status = TaskReminderStatus.Processing;
        ClaimToken = claimToken;
        ClaimedAt = now;
    }

    public void MarkSent(string providerResponseId, DateTimeOffset now)
    {
        AttemptCount++;
        Status = TaskReminderStatus.Sent;
        SentAt = now;
        ProviderResponseId = providerResponseId;
        LastError = null;
        ClaimToken = null;
        ClaimedAt = null;
    }

    public void ScheduleRetry(string error, DateTimeOffset nextAttemptAt)
    {
        AttemptCount++;
        RetryCount++;
        Status = TaskReminderStatus.Retry;
        LastError = error;
        NextAttemptAt = nextAttemptAt;
        ClaimToken = null;
        ClaimedAt = null;
    }

    public void MarkFailed(string error, DateTimeOffset now)
    {
        AttemptCount++;
        Status = TaskReminderStatus.Failed;
        LastError = error;
        AlertedAt = now;
        ClaimToken = null;
        ClaimedAt = null;
    }

    public void Cancel(string reason)
    {
        Status = TaskReminderStatus.Cancelled;
        CancellationReason = reason;
        ClaimToken = null;
        ClaimedAt = null;
    }
}
