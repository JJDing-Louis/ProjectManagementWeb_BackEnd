using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ProjectManagementWeb.Domain.Entities;

namespace ProjectManagementWeb.IntegrationTests;

internal sealed class FailNextCommentSaveInterceptor : SaveChangesInterceptor
{
    private int _remainingFailures;

    public void Arm() => Interlocked.Exchange(ref _remainingFailures, 1);

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ThrowWhenArmedForComment(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ThrowWhenArmedForComment(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void ThrowWhenArmedForComment(DbContext? context)
    {
        bool hasCommentMutation = context?.ChangeTracker.Entries<TaskItemComment>()
            .Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted) == true;
        if (hasCommentMutation && Interlocked.CompareExchange(ref _remainingFailures, 0, 1) == 1)
        {
            throw new InvalidOperationException("測試用留言儲存失敗。");
        }
    }
}
