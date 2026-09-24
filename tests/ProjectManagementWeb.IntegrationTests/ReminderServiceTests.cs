using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProjectManagementWeb.Application.Reminders;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Domain.Enums;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;
using TaskStatus = ProjectManagementWeb.Domain.Enums.TaskStatus;

namespace ProjectManagementWeb.IntegrationTests;

[NonParallelizable]
public sealed class ReminderServiceTests
{
    private readonly HashSet<Guid> _accountIds = [];
    private readonly HashSet<Guid> _projectIds = [];
    private readonly HashSet<Guid> _taskIds = [];
    private TestTimeProvider _timeProvider = null!;
    private TestWebApplicationFactory _factory = null!;
    private string _testConnectionString = null!;
    private string _masterConnectionString = null!;
    private string _databaseName = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        string? sourceConnectionString = Environment.GetEnvironmentVariable("PMW_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(sourceConnectionString))
        {
            Assert.Ignore("未設定 PMW_TEST_SQL_CONNECTION，略過實際 SQL Server reminder 測試。");
        }

        _databaseName = $"PMW_ReminderTest_{Guid.NewGuid():N}";
        var testBuilder = new SqlConnectionStringBuilder(sourceConnectionString) { InitialCatalog = _databaseName };
        var masterBuilder = new SqlConnectionStringBuilder(sourceConnectionString) { InitialCatalog = "master" };
        _testConnectionString = testBuilder.ConnectionString;
        _masterConnectionString = masterBuilder.ConnectionString;
        await ExecuteMasterCommandAsync($"CREATE DATABASE [{_databaseName}]");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(_testConnectionString)
            .Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (string.IsNullOrWhiteSpace(_databaseName))
        {
            return;
        }

        SqlConnection.ClearAllPools();
        await ExecuteMasterCommandAsync(
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]");
    }

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        _factory = new TestWebApplicationFactory(_testConnectionString, timeProvider: _timeProvider);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_factory is null)
        {
            return;
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.TaskReminders.Where(x => _taskIds.Contains(x.TaskItemId)).ExecuteDeleteAsync();
        await db.ProjectReminderRuns.Where(x => _projectIds.Contains(x.ProjectId)).ExecuteDeleteAsync();
        await db.TaskItems.IgnoreQueryFilters().Where(x => _taskIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.ProjectMemberRoles.Where(x => _projectIds.Contains(x.ProjectId)).ExecuteDeleteAsync();
        await db.ProjectMembers.Where(x => _projectIds.Contains(x.ProjectId)).ExecuteDeleteAsync();
        await db.Projects.IgnoreQueryFilters().Where(x => _projectIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.Set<ApplicationUserRole>().Where(x => _accountIds.Contains(x.UserId)).ExecuteDeleteAsync();
        await db.UserPreferences.Where(x => _accountIds.Contains(x.AccountId)).ExecuteDeleteAsync();
        await db.Users.Where(x => _accountIds.Contains(x.Id)).ExecuteDeleteAsync();
        await _factory.DisposeAsync();
    }

    // 測試案例：TC-ST-REM-001、TC-ST-REM-009（七日視窗、UTC/當地日期、摘要、重複與 cancellation）
    // 測試結果：Passed（完整 Integration 98/98，Failed 0，Skipped 0）
    // 上次測試時間：2026-09-16 20:47:27 +08:00
    [Test]
    public async Task 掃描應只建立到期前後三日提醒並提供可取消的正確摘要()
    {
        Guid recipientId = await CreateAccountAsync("window", enabled: true, emailConfirmed: true);
        Project project = await CreateProjectAsync(recipientId, "Asia/Taipei");
        DateOnly localDate = new(2026, 9, 16);
        foreach (int offset in Enumerable.Range(-4, 9))
        {
            await CreateTaskAsync(project.Id, recipientId, LocalDeadline(localDate.AddDays(offset), project.TimeZoneId));
        }

        ReminderScanSummary first = await ScanAsync();
        first.Should().Be(new ReminderScanSummary(1, 7, 0, 2, 0));

        await using (AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            TaskReminder[] reminders = await db.TaskReminders.Where(x => x.ProjectId == project.Id).ToArrayAsync();
            reminders.Should().HaveCount(7);
            reminders.Should().OnlyContain(x => x.ReminderDate == localDate && x.Status == TaskReminderStatus.Pending);
        }

        ReminderScanSummary duplicate = await ScanAsync();
        duplicate.Should().Be(new ReminderScanSummary(0, 0, 1, 0, 0));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> cancelled = () => ScanAsync(cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        Func<Task> cancelledSend = async () =>
        {
            await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IReminderService>()
                .SendDueAsync(1, cancellation.Token);
        };
        await cancelledSend.Should().ThrowAsync<OperationCanceledException>();
    }

    // 測試案例：TC-ST-REM-010、TC-ERR-REM-011（多時區當地 08:00、同日補跑、DST 與日期冪等）
    // 測試結果：Passed（完整 Integration 98/98，Failed 0，Skipped 0）
    // 上次測試時間：2026-09-16 20:47:27 +08:00
    [Test]
    public async Task 多時區掃描應在各Project當地八點後同日補跑且Dst不重複()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 1, 13, 0, 0, TimeSpan.Zero));
        Guid recipientId = await CreateAccountAsync("timezone", enabled: true, emailConfirmed: true);
        foreach (string timeZoneId in new[] { "America/New_York", "Asia/Tokyo", "UTC" })
        {
            Project project = await CreateProjectAsync(recipientId, timeZoneId);
            DateOnly localDate = LocalDate(_timeProvider.GetUtcNow(), timeZoneId);
            await CreateTaskAsync(project.Id, recipientId, LocalDeadline(localDate, timeZoneId));
        }
        Project beforeEight = await CreateProjectAsync(recipientId, "Pacific/Honolulu");
        await CreateTaskAsync(
            beforeEight.Id,
            recipientId,
            LocalDeadline(LocalDate(_timeProvider.GetUtcNow(), beforeEight.TimeZoneId), beforeEight.TimeZoneId));

        ReminderScanSummary first = await ScanAsync();
        first.Should().Be(new ReminderScanSummary(3, 3, 0, 0, 1));

        _timeProvider.Advance(TimeSpan.FromHours(1));
        ReminderScanSummary repeatedLocalDate = await ScanAsync();
        repeatedLocalDate.ProjectRuns.Should().Be(0);
        repeatedLocalDate.Duplicates.Should().Be(3);
        repeatedLocalDate.NotDue.Should().Be(1);

        _timeProvider.Advance(TimeSpan.FromHours(5));
        ReminderScanSummary sameDayRecovery = await ScanAsync();
        sameDayRecovery.ProjectRuns.Should().Be(1);
        sameDayRecovery.Created.Should().Be(1);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ProjectReminderRuns.GroupBy(x => new { x.ProjectId, x.ReminderDate })
            .AnyAsync(x => x.Count() > 1)).Should().BeFalse();
    }

    // 測試案例：TC-ERR-REM-002、TC-ERR-REM-003、TC-ST-REM-012（寄送前完成、帳號失效、改期、改派與軟刪除）
    // 測試結果：Passed（完整 Integration 98/98，Failed 0，Skipped 0）
    // 上次測試時間：2026-09-16 20:47:27 +08:00
    [Test]
    public async Task 寄送前重查應取消已失效提醒且不得呼叫Provider()
    {
        Guid disabledRecipientId = await CreateAccountAsync("disabled", enabled: false, emailConfirmed: true);
        Guid unverifiedRecipientId = await CreateAccountAsync("unverified", enabled: true, emailConfirmed: false);
        Project disabledProject = await CreateProjectAsync(disabledRecipientId, "Asia/Taipei");
        Project unverifiedProject = await CreateProjectAsync(unverifiedRecipientId, "Asia/Taipei");
        DateOnly initialReminderDate = LocalDate(_timeProvider.GetUtcNow(), "Asia/Taipei");
        TaskItem disabledTask = await CreateTaskAsync(
            disabledProject.Id,
            disabledRecipientId,
            LocalDeadline(initialReminderDate, disabledProject.TimeZoneId));
        TaskItem unverifiedTask = await CreateTaskAsync(
            unverifiedProject.Id,
            unverifiedRecipientId,
            LocalDeadline(initialReminderDate, unverifiedProject.TimeZoneId));
        ReminderScanSummary invalidRecipientScan = await ScanAsync();
        invalidRecipientScan.Created.Should().Be(0);
        invalidRecipientScan.Skipped.Should().Be(2);
        await using (AsyncServiceScope invalidVerifyScope = _factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext invalidVerify = invalidVerifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await invalidVerify.TaskReminders.CountAsync(x =>
                x.TaskItemId == disabledTask.Id || x.TaskItemId == unverifiedTask.Id)).Should().Be(0);
        }

        Guid recipientId = await CreateAccountAsync("recheck", enabled: true, emailConfirmed: true);
        Guid newRecipientId = await CreateAccountAsync("reassigned", enabled: true, emailConfirmed: true);
        Project project = await CreateProjectAsync(recipientId, "Asia/Taipei");
        DateOnly reminderDate = LocalDate(_timeProvider.GetUtcNow(), project.TimeZoneId);

        TaskItem completed = await CreateTaskAsync(project.Id, recipientId, LocalDeadline(reminderDate, project.TimeZoneId));
        TaskItem rescheduled = await CreateTaskAsync(project.Id, recipientId, LocalDeadline(reminderDate, project.TimeZoneId));
        TaskItem reassigned = await CreateTaskAsync(project.Id, recipientId, LocalDeadline(reminderDate, project.TimeZoneId));
        TaskItem deleted = await CreateTaskAsync(project.Id, recipientId, LocalDeadline(reminderDate, project.TimeZoneId));
        await SeedReminderAsync(project.Id, completed.Id, recipientId, reminderDate);
        await SeedReminderAsync(project.Id, rescheduled.Id, recipientId, reminderDate);
        await SeedReminderAsync(project.Id, reassigned.Id, recipientId, reminderDate);
        await SeedReminderAsync(project.Id, deleted.Id, recipientId, reminderDate);

        await using (AsyncServiceScope mutateScope = _factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = mutateScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            DateTimeOffset now = _timeProvider.GetUtcNow();
            (await db.TaskItems.SingleAsync(x => x.Id == completed.Id)).UpdateStatus(TaskStatus.Completed, now);
            TaskItem rescheduledEntity = await db.TaskItems.SingleAsync(x => x.Id == rescheduled.Id);
            rescheduledEntity.Update(
                rescheduledEntity.Title,
                rescheduledEntity.Description,
                recipientId,
                rescheduledEntity.StartAt,
                LocalDeadline(reminderDate.AddDays(10), project.TimeZoneId),
                rescheduledEntity.Status,
                now);
            TaskItem reassignedEntity = await db.TaskItems.SingleAsync(x => x.Id == reassigned.Id);
            reassignedEntity.Update(
                reassignedEntity.Title,
                reassignedEntity.Description,
                newRecipientId,
                reassignedEntity.StartAt,
                reassignedEntity.Deadline,
                reassignedEntity.Status,
                now);
            (await db.TaskItems.SingleAsync(x => x.Id == deleted.Id)).SoftDelete(now);
            await db.SaveChangesAsync();
        }

        ReminderSendSummary result = await SendAsync(10);
        result.Should().Be(new ReminderSendSummary(4, 0, 0, 0, 4));
        _factory.EmailGateway.Recipients.Should().BeEmpty();

        await using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        string[] reasons = await verify.TaskReminders.Where(x => x.ProjectId == project.Id)
            .Select(x => x.CancellationReason!)
            .ToArrayAsync();
        reasons.Should().BeEquivalentTo("TaskCompleted", "DeadlineOutsideWindow", "RecipientChanged", "TaskDeleted");

        ReminderScanSummary latestAssignmentScan = await ScanAsync();
        latestAssignmentScan.Created.Should().Be(1);
        TaskReminder reassignedReminder = await verify.TaskReminders.SingleAsync(x =>
            x.TaskItemId == reassigned.Id && x.RecipientAccountId == newRecipientId);
        reassignedReminder.ReminderDate.Should().Be(reminderDate);
    }

    // 測試案例：TC-ERR-REM-004、TC-F-REM-005（並行 scanner/worker 去重、成功判定、內容與 provider response）
    // 測試結果：Passed（完整 Integration 98/98，Failed 0，Skipped 0）
    // 上次測試時間：2026-09-16 20:47:27 +08:00
    [Test]
    public async Task 並行掃描與寄送應最多建立並寄出一次且完整記錄成功()
    {
        Guid recipientId = await CreateAccountAsync("concurrency", enabled: true, emailConfirmed: true);
        Project project = await CreateProjectAsync(recipientId, "Asia/Taipei");
        DateOnly reminderDate = LocalDate(_timeProvider.GetUtcNow(), project.TimeZoneId);
        TaskItem task = await CreateTaskAsync(project.Id, recipientId, LocalDeadline(reminderDate, project.TimeZoneId));

        ReminderScanSummary[] scans = await Task.WhenAll(ScanAsync(), ScanAsync());
        scans.Sum(x => x.ProjectRuns).Should().Be(1);
        scans.Sum(x => x.Created).Should().Be(1);

        ReminderSendSummary[] sends = await Task.WhenAll(SendAsync(1), SendAsync(1));
        sends.Sum(x => x.Claimed).Should().Be(1);
        sends.Sum(x => x.Sent).Should().Be(1);
        _factory.EmailGateway.Recipients.Should().ContainSingle();
        _factory.EmailGateway.Bodies.Should().ContainSingle().Which
            .Should().Contain(task.Title).And.Contain(task.Deadline.ToString("O"))
            .And.Contain($"/projects/{project.Id}/task-items/{task.Id}");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        TaskReminder reminder = await db.TaskReminders.SingleAsync(x => x.TaskItemId == task.Id);
        reminder.Status.Should().Be(TaskReminderStatus.Sent);
        reminder.AttemptCount.Should().Be(1);
        reminder.SentAt.Should().Be(_timeProvider.GetUtcNow());
        reminder.ProviderResponseId.Should().Be($"test-response-{reminder.Id:N}");
        _factory.EmailGateway.IdempotencyKeys.Should().Equal(reminder.Id.ToString("N"));
    }

    // 測試案例：TC-ST-REM-006、TC-ERR-REM-011（5/15/60 分鐘三次 retry、最終 DB 狀態與安全結構化 Log）
    // 測試結果：Passed（完整 Integration 98/98，Failed 0，Skipped 0）
    // 上次測試時間：2026-09-16 20:47:27 +08:00
    [Test]
    public async Task 持續寄送失敗應精確重試三次後標記Failed並寫安全Log()
    {
        TaskReminder reminder = await SeedEligibleReminderAsync("retry-failed");
        _factory.EmailGateway.ShouldFail = true;

        (await SendAsync(1)).Retried.Should().Be(1);
        await AssertRetryStateAsync(reminder.Id, 1, 1, TimeSpan.FromMinutes(5));
        _timeProvider.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        (await SendAsync(1)).Claimed.Should().Be(0);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));

        (await SendAsync(1)).Retried.Should().Be(1);
        await AssertRetryStateAsync(reminder.Id, 2, 2, TimeSpan.FromMinutes(15));
        _timeProvider.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        (await SendAsync(1)).Claimed.Should().Be(0);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));

        (await SendAsync(1)).Retried.Should().Be(1);
        await AssertRetryStateAsync(reminder.Id, 3, 3, TimeSpan.FromMinutes(60));
        _timeProvider.Advance(TimeSpan.FromMinutes(60) - TimeSpan.FromSeconds(1));
        (await SendAsync(1)).Claimed.Should().Be(0);
        _timeProvider.Advance(TimeSpan.FromSeconds(1));

        (await SendAsync(1)).Failed.Should().Be(1);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        TaskReminder failed = await db.TaskReminders.SingleAsync(x => x.Id == reminder.Id);
        failed.Status.Should().Be(TaskReminderStatus.Failed);
        failed.AttemptCount.Should().Be(4);
        failed.RetryCount.Should().Be(3);
        failed.AlertedAt.Should().Be(_timeProvider.GetUtcNow());
        failed.LastError.Should().Be(nameof(InvalidOperationException));
        _factory.LogSink.Entries.Should().Contain(x =>
            x.Level == LogLevel.Warning &&
            x.Message.Contains(reminder.Id.ToString(), StringComparison.Ordinal) &&
            x.Message.Contains("ProviderSendFailed", StringComparison.Ordinal) &&
            !x.Message.Contains("@example.test", StringComparison.Ordinal) &&
            !x.Message.Contains("測試用 Email gateway 失敗", StringComparison.Ordinal));
    }

    // 測試案例：TC-ST-REM-007（初次失敗、第一次 retry 成功）
    // 測試結果：Passed（完整 Integration 98/98，Failed 0，Skipped 0）
    // 上次測試時間：2026-09-16 20:47:27 +08:00
    [Test]
    public async Task 重試成功後應標記Sent並停止後續重試()
    {
        TaskReminder reminder = await SeedEligibleReminderAsync("retry-success");
        _factory.EmailGateway.ShouldFail = true;
        (await SendAsync(1)).Retried.Should().Be(1);
        _factory.EmailGateway.ShouldFail = false;
        _timeProvider.Advance(TimeSpan.FromMinutes(5));

        (await SendAsync(1)).Sent.Should().Be(1);
        _timeProvider.Advance(TimeSpan.FromHours(2));
        (await SendAsync(1)).Claimed.Should().Be(0);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        TaskReminder sent = await db.TaskReminders.SingleAsync(x => x.Id == reminder.Id);
        sent.Status.Should().Be(TaskReminderStatus.Sent);
        sent.AttemptCount.Should().Be(2);
        sent.RetryCount.Should().Be(1);
        sent.ProviderResponseId.Should().NotBeNullOrWhiteSpace();
    }

    // 測試案例：TC-ERR-REM-008（provider 接受但 Sent 寫回失敗，以固定 idempotency key 重試）
    // 測試結果：Passed（完整 Integration 98/98，Failed 0，Skipped 0）
    // 上次測試時間：2026-09-16 20:47:27 +08:00
    [Test]
    public async Task Provider接受但成功寫回失敗時不得誤標Sent且應以相同Key重試()
    {
        await _factory.DisposeAsync();
        _factory = new TestWebApplicationFactory(
            _testConnectionString,
            saveChangesInterceptor: new FailTaskReminderSentSaveInterceptor(),
            timeProvider: _timeProvider);
        TaskReminder reminder = await SeedEligibleReminderAsync("writeback");

        ReminderSendSummary first = await SendAsync(1);
        first.Should().Be(new ReminderSendSummary(1, 0, 1, 0, 0));
        await using (AsyncServiceScope firstScope = _factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = firstScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            TaskReminder retry = await db.TaskReminders.SingleAsync(x => x.Id == reminder.Id);
            retry.Status.Should().Be(TaskReminderStatus.Retry);
            retry.SentAt.Should().BeNull();
            retry.ProviderResponseId.Should().BeNull();
            retry.LastError.Should().Be(nameof(InvalidOperationException));
        }

        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        (await SendAsync(1)).Sent.Should().Be(1);
        _factory.EmailGateway.IdempotencyKeys.Should().Equal(
            reminder.Id.ToString("N"),
            reminder.Id.ToString("N"));
    }

    // 測試案例：TC-ST-REM-009（Hangfire scanner／sender recurring jobs 實際註冊）
    // 測試結果：Passed（完整 Integration 98/98，Failed 0，Skipped 0）
    // 上次測試時間：2026-09-16 20:47:27 +08:00
    [Test]
    public async Task 啟用ReminderJobs時應在Hangfire註冊Scanner與Sender()
    {
        await _factory.DisposeAsync();
        _factory = new TestWebApplicationFactory(
            _testConnectionString,
            timeProvider: _timeProvider,
            enableReminderJobs: true);
        using HttpClient client = _factory.CreateClient();
        (await client.GetAsync("/health")).IsSuccessStatusCode.Should().BeTrue();

        await using var connection = new SqlConnection(_testConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM [HangFire].[Set]
            WHERE [Key] = 'recurring-jobs'
              AND [Value] IN ('task-reminder-scan', 'task-reminder-send')
            """;
        Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(2);
    }

    private async Task<TaskReminder> SeedEligibleReminderAsync(string marker)
    {
        Guid recipientId = await CreateAccountAsync(marker, enabled: true, emailConfirmed: true);
        Project project = await CreateProjectAsync(recipientId, "Asia/Taipei");
        DateOnly reminderDate = LocalDate(_timeProvider.GetUtcNow(), project.TimeZoneId);
        TaskItem task = await CreateTaskAsync(project.Id, recipientId, LocalDeadline(reminderDate, project.TimeZoneId));
        return await SeedReminderAsync(project.Id, task.Id, recipientId, reminderDate);
    }

    private async Task AssertRetryStateAsync(Guid reminderId, int attemptCount, int retryCount, TimeSpan delay)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        TaskReminder reminder = await db.TaskReminders.SingleAsync(x => x.Id == reminderId);
        reminder.Status.Should().Be(TaskReminderStatus.Retry);
        reminder.AttemptCount.Should().Be(attemptCount);
        reminder.RetryCount.Should().Be(retryCount);
        reminder.NextAttemptAt.Should().Be(_timeProvider.GetUtcNow() + delay);
    }

    private async Task<ReminderScanSummary> ScanAsync(CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IReminderService>().ScanAsync(cancellationToken);
    }

    private async Task<ReminderSendSummary> SendAsync(int maximumCount)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IReminderService>()
            .SendDueAsync(maximumCount, CancellationToken.None);
    }

    private async Task<Guid> CreateAccountAsync(string marker, bool enabled, bool emailConfirmed)
    {
        Guid id = Guid.NewGuid();
        string suffix = Guid.NewGuid().ToString("N");
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = $"reminder-{marker}-{suffix}"[..Math.Min(100, $"reminder-{marker}-{suffix}".Length)],
            NormalizedUserName = $"REMINDER-{marker}-{suffix}".ToUpperInvariant(),
            Email = $"{suffix}@example.test",
            NormalizedEmail = $"{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            EmailConfirmed = emailConfirmed,
            IsEnabled = enabled,
            SecurityStamp = suffix,
            ConcurrencyStamp = suffix
        });
        await db.SaveChangesAsync();
        _accountIds.Add(id);
        return id;
    }

    private async Task<Project> CreateProjectAsync(Guid ownerId, string timeZoneId)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var project = new Project(
            Guid.NewGuid(),
            $"PRJ-R{Guid.NewGuid():N}"[..20],
            $"Reminder {timeZoneId} {Guid.NewGuid():N}",
            null,
            ownerId,
            timeZoneId,
            now);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        _projectIds.Add(project.Id);
        return project;
    }

    private async Task<TaskItem> CreateTaskAsync(Guid projectId, Guid recipientId, DateTimeOffset deadline)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var task = new TaskItem(
            Guid.NewGuid(),
            $"TASK-R{Guid.NewGuid():N}"[..22],
            projectId,
            recipientId,
            recipientId,
            $"Reminder Task {Guid.NewGuid():N}",
            null,
            deadline.AddDays(-1),
            deadline,
            now);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.TaskItems.Add(task);
        await db.SaveChangesAsync();
        _taskIds.Add(task.Id);
        return task;
    }

    private async Task<TaskReminder> SeedReminderAsync(
        Guid projectId,
        Guid taskId,
        Guid recipientId,
        DateOnly reminderDate)
    {
        var reminder = new TaskReminder(
            Guid.NewGuid(),
            projectId,
            taskId,
            recipientId,
            reminderDate,
            _timeProvider.GetUtcNow());
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.TaskReminders.Add(reminder);
        await db.SaveChangesAsync();
        return reminder;
    }

    private static DateOnly LocalDate(DateTimeOffset utcNow, string timeZoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).Date);

    private static DateTimeOffset LocalDeadline(DateOnly date, string timeZoneId)
    {
        DateTime local = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Unspecified);
        DateTime utc = TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private async Task ExecuteMasterCommandAsync(string commandText)
    {
        await using var connection = new SqlConnection(_masterConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }
}
