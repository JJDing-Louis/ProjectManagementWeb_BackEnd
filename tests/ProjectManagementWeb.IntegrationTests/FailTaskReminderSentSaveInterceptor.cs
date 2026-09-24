using Microsoft.EntityFrameworkCore.Diagnostics;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Domain.Enums;

namespace ProjectManagementWeb.IntegrationTests;

internal sealed class FailTaskReminderSentSaveInterceptor : SaveChangesInterceptor
{
    private int _remainingFailures = 1;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null &&
            eventData.Context.ChangeTracker.Entries<TaskReminder>()
                .Any(x => x.Entity.Status == TaskReminderStatus.Sent) &&
            Interlocked.CompareExchange(ref _remainingFailures, 0, 1) == 1)
        {
            throw new InvalidOperationException("測試用 Sent 寫回失敗。");
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
