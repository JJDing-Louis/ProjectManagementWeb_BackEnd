using ProjectManagementWeb.Application.Reminders;

namespace ProjectManagementWeb.Infrastructure.Jobs;

public sealed class ReminderJobs
{
    private readonly IReminderService _reminderService;

    public ReminderJobs(IReminderService reminderService) => _reminderService = reminderService;

    public Task ScanAsync(CancellationToken cancellationToken) =>
        _reminderService.ScanAsync(cancellationToken);

    public Task SendAsync(CancellationToken cancellationToken) =>
        _reminderService.SendDueAsync(100, cancellationToken);
}
