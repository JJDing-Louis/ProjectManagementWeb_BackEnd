namespace ProjectManagementWeb.Application.Reminders;

public sealed record ReminderScanSummary(
    int ProjectRuns,
    int Created,
    int Duplicates,
    int Skipped,
    int NotDue);

public sealed record ReminderSendSummary(
    int Claimed,
    int Sent,
    int Retried,
    int Failed,
    int Cancelled);

public interface IReminderService
{
    Task<ReminderScanSummary> ScanAsync(CancellationToken cancellationToken);
    Task<ReminderSendSummary> SendDueAsync(int maximumCount, CancellationToken cancellationToken);
}
