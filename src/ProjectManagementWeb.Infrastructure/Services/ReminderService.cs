using System.Data;
using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProjectManagementWeb.Application.Reminders;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Domain.Enums;
using ProjectManagementWeb.Infrastructure.Email;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;
using TaskStatus = ProjectManagementWeb.Domain.Enums.TaskStatus;

namespace ProjectManagementWeb.Infrastructure.Services;

internal sealed class ReminderService : IReminderService
{
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60)
    ];

    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IEmailGateway _emailGateway;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReminderService> _logger;
    private readonly string _frontendBaseUrl;

    public ReminderService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IEmailGateway emailGateway,
        TimeProvider timeProvider,
        ILogger<ReminderService> logger,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _emailGateway = emailGateway;
        _timeProvider = timeProvider;
        _logger = logger;
        _frontendBaseUrl = (configuration["Frontend:BaseUrl"] ??
                            configuration["Smtp:FrontendBaseUrl"] ??
                            "http://localhost:5173").TrimEnd('/');
    }

    public async Task<ReminderScanSummary> ScanAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await using ApplicationDbContext projectDb = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var projects = await projectDb.Projects.AsNoTracking()
            .Select(x => new { x.Id, x.TimeZoneId })
            .ToArrayAsync(cancellationToken);

        int projectRuns = 0;
        int created = 0;
        int duplicates = 0;
        int skipped = 0;
        int notDue = 0;

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(project.TimeZoneId);
            DateTimeOffset localNow = TimeZoneInfo.ConvertTime(now, timeZone);
            if (localNow.TimeOfDay < TimeSpan.FromHours(8))
            {
                notDue++;
                continue;
            }

            DateOnly reminderDate = DateOnly.FromDateTime(localNow.Date);
            ProjectScanResult result = await ScanProjectAsync(
                project.Id,
                timeZone,
                reminderDate,
                now,
                cancellationToken);
            projectRuns += result.Executed ? 1 : 0;
            created += result.Created;
            duplicates += result.Duplicates;
            skipped += result.Skipped;
        }

        var summary = new ReminderScanSummary(projectRuns, created, duplicates, skipped, notDue);
        _logger.LogInformation(
            "Task reminder scan completed. ProjectRuns={ProjectRuns} Created={Created} Duplicates={Duplicates} Skipped={Skipped} NotDue={NotDue}",
            summary.ProjectRuns,
            summary.Created,
            summary.Duplicates,
            summary.Skipped,
            summary.NotDue);
        return summary;
    }

    public async Task<ReminderSendSummary> SendDueAsync(int maximumCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        int claimed = 0;
        int sent = 0;
        int retried = 0;
        int failed = 0;
        int cancelled = 0;

        for (int index = 0; index < maximumCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid? reminderId = await ClaimNextAsync(cancellationToken);
            if (reminderId is null)
            {
                break;
            }

            claimed++;
            ReminderSendOutcome outcome = await SendClaimedAsync(reminderId.Value, cancellationToken);
            sent += outcome == ReminderSendOutcome.Sent ? 1 : 0;
            retried += outcome == ReminderSendOutcome.Retry ? 1 : 0;
            failed += outcome == ReminderSendOutcome.Failed ? 1 : 0;
            cancelled += outcome == ReminderSendOutcome.Cancelled ? 1 : 0;
        }

        return new ReminderSendSummary(claimed, sent, retried, failed, cancelled);
    }

    private async Task<ProjectScanResult> ScanProjectAsync(
        Guid projectId,
        TimeZoneInfo timeZone,
        DateOnly reminderDate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (await db.ProjectReminderRuns.AnyAsync(x =>
                x.ProjectId == projectId && x.ReminderDate == reminderDate,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ProjectScanResult(false, 0, 1, 0);
        }

        var run = new ProjectReminderRun(Guid.NewGuid(), projectId, reminderDate, now);
        db.ProjectReminderRuns.Add(run);

        var candidates = await (
                from task in db.TaskItems
                join recipient in db.Users on task.AssignedAccountId equals recipient.Id
                where task.ProjectId == projectId && task.Status != TaskStatus.Completed
                select new
                {
                    task.Id,
                    task.AssignedAccountId,
                    task.Deadline,
                    recipient.IsEnabled,
                    recipient.EmailConfirmed
                })
            .ToArrayAsync(cancellationToken);

        int created = 0;
        int duplicates = 0;
        int skipped = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateOnly deadlineDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(candidate.Deadline, timeZone).Date);
            int dayDifference = deadlineDate.DayNumber - reminderDate.DayNumber;
            if (dayDifference is < -3 or > 3 || !candidate.IsEnabled || !candidate.EmailConfirmed)
            {
                skipped++;
                continue;
            }

            bool alreadyExists = await db.TaskReminders.AnyAsync(x =>
                x.TaskItemId == candidate.Id &&
                x.RecipientAccountId == candidate.AssignedAccountId &&
                x.ReminderDate == reminderDate,
                cancellationToken);
            if (alreadyExists)
            {
                duplicates++;
                continue;
            }

            db.TaskReminders.Add(new TaskReminder(
                Guid.NewGuid(),
                projectId,
                candidate.Id,
                candidate.AssignedAccountId,
                reminderDate,
                now));
            created++;
        }

        run.Complete(created, duplicates, skipped, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ProjectScanResult(true, created, duplicates, skipped);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraint(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ProjectScanResult(false, 0, 1, 0);
        }
    }

    private async Task<Guid?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset expiredClaim = now - ClaimLease;
        await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        TaskReminder? reminder = await db.TaskReminders
            .FromSqlInterpolated($$"""
                SELECT TOP(1) *
                FROM [TaskReminders] WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE (([Status] IN ('Pending', 'Retry') AND [NextAttemptAt] <= {{now}})
                       OR ([Status] = 'Processing' AND [ClaimedAt] < {{expiredClaim}}))
                ORDER BY [NextAttemptAt], [CreatedAt]
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (reminder is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        reminder.Claim(Guid.NewGuid(), now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return reminder.Id;
    }

    private async Task<ReminderSendOutcome> SendClaimedAsync(Guid reminderId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var data = await (
                from reminder in db.TaskReminders
                join task in db.TaskItems.IgnoreQueryFilters() on reminder.TaskItemId equals task.Id
                join project in db.Projects.IgnoreQueryFilters() on reminder.ProjectId equals project.Id
                join recipient in db.Users on reminder.RecipientAccountId equals recipient.Id
                where reminder.Id == reminderId && reminder.Status == TaskReminderStatus.Processing
                select new { Reminder = reminder, Task = task, Project = project, Recipient = recipient })
            .SingleAsync(cancellationToken);

        string? cancellationReason = GetCancellationReason(
            data.Reminder,
            data.Task,
            data.Project,
            data.Recipient);
        if (cancellationReason is not null)
        {
            data.Reminder.Cancel(cancellationReason);
            await db.SaveChangesAsync(cancellationToken);
            return ReminderSendOutcome.Cancelled;
        }

        string recipientEmail = data.Recipient.Email!;
        string subject = $"Task 到期提醒：{data.Task.Title}";
        string link = $"{_frontendBaseUrl}/projects/{data.Project.Id}/task-items/{data.Task.Id}";
        string body = $"<p>Task：{WebUtility.HtmlEncode(data.Task.Title)}</p>" +
                      $"<p>期限：{WebUtility.HtmlEncode(data.Task.Deadline.ToString("O"))}</p>" +
                      $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">查看 Task</a></p>";

        try
        {
            EmailSendResult result = await _emailGateway.SendAsync(
                recipientEmail,
                subject,
                body,
                data.Reminder.Id.ToString("N"),
                cancellationToken);
            data.Reminder.MarkSent(result.ResponseId, now);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ReminderSendOutcome.Sent;
            }
            catch (Exception writeException) when (writeException is DbUpdateException or InvalidOperationException)
            {
                return await RecordSendFailureAsync(
                    reminderId,
                    "ProviderAcceptedButWriteBackFailed",
                    writeException,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception sendException)
        {
            return await RecordSendFailureAsync(
                reminderId,
                "ProviderSendFailed",
                sendException,
                cancellationToken);
        }
    }

    private async Task<ReminderSendOutcome> RecordSendFailureAsync(
        Guid reminderId,
        string failureType,
        Exception exception,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await using ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        TaskReminder reminder = await db.TaskReminders.SingleAsync(x => x.Id == reminderId, cancellationToken);
        string safeError = exception.GetType().Name;
        if (reminder.RetryCount < RetryDelays.Length)
        {
            reminder.ScheduleRetry(safeError, now + RetryDelays[reminder.RetryCount]);
            await db.SaveChangesAsync(cancellationToken);
            return ReminderSendOutcome.Retry;
        }

        reminder.MarkFailed(safeError, now);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(
            "Task reminder delivery permanently failed. ReminderId={ReminderId} TaskItemId={TaskItemId} AttemptCount={AttemptCount} FailureType={FailureType}",
            reminder.Id,
            reminder.TaskItemId,
            reminder.AttemptCount,
            failureType);
        return ReminderSendOutcome.Failed;
    }

    private static string? GetCancellationReason(
        TaskReminder reminder,
        TaskItem task,
        Project project,
        ApplicationUser recipient)
    {
        if (project.DeletedAt is not null)
        {
            return "ProjectDeleted";
        }
        if (task.DeletedAt is not null)
        {
            return "TaskDeleted";
        }
        if (task.Status == TaskStatus.Completed)
        {
            return "TaskCompleted";
        }
        if (task.AssignedAccountId != reminder.RecipientAccountId)
        {
            return "RecipientChanged";
        }
        if (!recipient.IsEnabled || !recipient.EmailConfirmed || string.IsNullOrWhiteSpace(recipient.Email))
        {
            return "RecipientUnavailable";
        }

        TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(project.TimeZoneId);
        DateOnly deadlineDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(task.Deadline, timeZone).Date);
        int dayDifference = deadlineDate.DayNumber - reminder.ReminderDate.DayNumber;
        return dayDifference is < -3 or > 3 ? "DeadlineOutsideWindow" : null;
    }

    private static bool IsUniqueConstraint(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private sealed record ProjectScanResult(bool Executed, int Created, int Duplicates, int Skipped);

    private enum ReminderSendOutcome
    {
        Sent,
        Retry,
        Failed,
        Cancelled
    }
}
