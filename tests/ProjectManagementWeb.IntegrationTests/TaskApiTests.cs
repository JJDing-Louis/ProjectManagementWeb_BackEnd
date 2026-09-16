using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Domain.Enums;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;
using TaskStatus = ProjectManagementWeb.Domain.Enums.TaskStatus;

namespace ProjectManagementWeb.IntegrationTests;

[NonParallelizable]
public sealed partial class TaskApiTests
{
    private const string ValidPassword = "Test_password123!";
    private readonly HashSet<Guid> _accountIds = [];
    private readonly HashSet<Guid> _projectIds = [];
    private readonly HashSet<Guid> _taskIds = [];
    private readonly HashSet<Guid> _commentIds = [];
    private TestWebApplicationFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        string? connectionString = Environment.GetEnvironmentVariable("PMW_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Ignore("未設定 PMW_TEST_SQL_CONNECTION，略過實際 SQL Server Task API 測試。");
        }

        _factory = new TestWebApplicationFactory(connectionString);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_factory is null)
        {
            return;
        }

        await CleanupTestDataAsync();
        await _factory.DisposeAsync();
    }

    // 測試案例：TC-F-TASK-001、TC-F-TASK-002、TC-E-TASK-004（清單欄位、交集篩選、分頁與排序）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Task清單應正確呈現欄位並套用搜尋篩選分頁與排序()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid assigneeId, _) = await CreateUserAsync(SystemRoles.User);
        (Guid otherAssigneeId, _) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, [assigneeId, otherAssigneeId]);
        string marker = Guid.NewGuid().ToString("N")[..8];
        DateTimeOffset origin = DateTimeOffset.UtcNow.AddDays(-10);
        TaskItem oldest = await CreateTaskAsync(project.Id, ownerId, assigneeId, $"標題-{marker}-old", "描述-old", origin,
            origin.AddDays(7), TaskStatus.Pending);
        TaskItem middle = await CreateTaskAsync(project.Id, ownerId, assigneeId, "中間標題", $"描述-{marker}", origin.AddDays(1),
            origin.AddDays(4), TaskStatus.Blocked);
        TaskItem newest = await CreateTaskAsync(project.Id, ownerId, otherAssigneeId, marker, "最新描述", origin.AddDays(2),
            origin.AddDays(3), TaskStatus.Completed);
        TaskItem deleted = await CreateTaskAsync(project.Id, ownerId, assigneeId, $"刪除-{marker}", null, origin.AddDays(3),
            origin.AddDays(5), TaskStatus.Pending, deleted: true);
        using HttpClient client = await CreateAuthenticatedClientAsync(ownerAccount);

        JsonElement defaults = await GetJsonAsync(client, $"/api/v1/projects/{project.Id}/task-items?pageSize=100");
        defaults.GetProperty("items").GetArrayLength().Should().Be(3);
        defaults.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid())
            .Should().Equal(newest.Id, middle.Id, oldest.Id);
        JsonElement row = defaults.GetProperty("items")[0];
        row.EnumerateObject().Select(x => x.Name).Should().Contain([
            "id", "code", "title", "deadline", "status", "createdByAccountId", "assignedAccountId", "createdAt"
        ]);
        defaults.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid())
            .Should().NotContain(deleted.Id);

        foreach (string search in new[] { oldest.Code, $"標題-{marker}", $"描述-{marker}" })
        {
            JsonElement found = await GetJsonAsync(client,
                $"/api/v1/projects/{project.Id}/task-items?search={Uri.EscapeDataString(search)}&pageSize=100");
            found.GetProperty("totalCount").GetInt32().Should().Be(1);
        }

        JsonElement intersection = await GetJsonAsync(client,
            $"/api/v1/projects/{project.Id}/task-items?status=Blocked&assignedAccountId={assigneeId}&onlyMine=false&page=0&pageSize=500");
        intersection.GetProperty("page").GetInt32().Should().Be(1);
        intersection.GetProperty("pageSize").GetInt32().Should().Be(100);
        intersection.GetProperty("totalCount").GetInt32().Should().Be(1);
        intersection.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(middle.Id);

        using HttpClient assignee = await CreateAuthenticatedClientAsync((await GetAccountAsync(assigneeId))!);
        JsonElement mine = await GetJsonAsync(assignee,
            $"/api/v1/projects/{project.Id}/task-items?onlyMine=true&pageSize=100");
        mine.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid())
            .Should().BeEquivalentTo(new[] { oldest.Id, middle.Id });

        foreach (string sortBy in new[] { "deadline", "status", "code", "createdAt" })
        {
            JsonElement asc = await GetJsonAsync(client,
                $"/api/v1/projects/{project.Id}/task-items?sortBy={sortBy}&sortDirection=asc&pageSize=100");
            JsonElement desc = await GetJsonAsync(client,
                $"/api/v1/projects/{project.Id}/task-items?sortBy={sortBy}&sortDirection=desc&pageSize=100");
            asc.GetProperty("items").GetArrayLength().Should().Be(3);
            desc.GetProperty("items").GetArrayLength().Should().Be(3);
            asc.GetProperty("items")[0].GetProperty("id").GetGuid()
                .Should().NotBe(desc.GetProperty("items")[0].GetProperty("id").GetGuid());
        }

        JsonElement fallback = await GetJsonAsync(client,
            $"/api/v1/projects/{project.Id}/task-items?sortBy=createdAt%5D%3BDROP%20TABLE&sortDirection=unknown&pageSize=100");
        fallback.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid())
            .Should().Equal(newest.Id, middle.Id, oldest.Id);
        JsonElement empty = await GetJsonAsync(client, $"/api/v1/projects/{project.Id}/task-items?search=no-such-{marker}");
        empty.GetProperty("totalCount").GetInt32().Should().Be(0);
        empty.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // 測試案例：TC-ERR-TASK-005、TC-F-TASK-021（Project scope、403／404 優先序與詳情欄位）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Task詳情應完整且跨Project與非成員存取應遵守403與404優先序()
    {
        (Guid ownerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid memberId, string memberAccount) = await CreateUserAsync(SystemRoles.User);
        (Guid viewerId, string viewerAccount) = await CreateUserAsync(SystemRoles.Viewer);
        (_, string outsiderAccount) = await CreateUserAsync(SystemRoles.User);
        Project projectA = await CreateProjectAsync(ownerId, [memberId, viewerId]);
        Project projectB = await CreateProjectAsync(ownerId, []);
        TaskItem task = await CreateTaskAsync(projectA.Id, ownerId, memberId, "完整詳情", "完整描述",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(2), TaskStatus.InProgress);

        using HttpClient member = await CreateAuthenticatedClientAsync(memberAccount);
        JsonElement detail = await GetJsonAsync(member, $"/api/v1/projects/{projectA.Id}/task-items/{task.Id}");
        detail.EnumerateObject().Select(x => x.Name).Should().Contain([
            "id", "code", "projectId", "title", "description", "createdByAccountId", "assignedAccountId",
            "startAt", "deadline", "status", "createdAt", "updatedAt", "rowVersion"
        ]);
        await AssertProblemAsync(await member.GetAsync($"/api/v1/projects/{projectA.Id}/task-items/{Guid.NewGuid()}"),
            HttpStatusCode.NotFound, "not_found");
        await AssertProblemAsync(await member.GetAsync($"/api/v1/projects/{projectB.Id}/task-items/{task.Id}"),
            HttpStatusCode.Forbidden, "forbidden");

        using HttpClient outsider = await CreateAuthenticatedClientAsync(outsiderAccount);
        foreach (string path in new[]
                 {
                     $"/api/v1/projects/{projectA.Id}/task-items",
                     $"/api/v1/projects/{projectA.Id}/task-items/{task.Id}",
                     $"/api/v1/projects/{projectA.Id}/task-items/{task.Id}/comments"
                 })
        {
            await AssertProblemAsync(await outsider.GetAsync(path), HttpStatusCode.Forbidden, "forbidden");
        }

        using HttpClient viewer = await CreateAuthenticatedClientAsync(viewerAccount);
        JsonElement viewerDetail = await GetJsonAsync(viewer, $"/api/v1/projects/{projectA.Id}/task-items/{task.Id}");
        viewerDetail.GetProperty("id").GetGuid().Should().Be(task.Id);
    }

    // 測試案例：TC-SEC-API-005（Task 與 Comment 全 endpoint 的 403／404 優先序）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:39:03 +08:00
    [Test]
    public async Task Task與Comment範圍端點應先授權再判斷Project是否存在()
    {
        (Guid ownerId, string adminAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid assigneeId, _) = await CreateUserAsync(SystemRoles.User);
        (_, string outsiderAccount) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, [assigneeId]);
        TaskItem task = await CreateTaskAsync(
            project.Id,
            ownerId,
            assigneeId,
            "Scope 矩陣 Task",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(1),
            TaskStatus.Pending);
        Guid missingProjectId = Guid.NewGuid();
        Guid commentId = Guid.NewGuid();
        string rowVersion;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            rowVersion = Convert.ToBase64String(
                await db.TaskItems.Where(x => x.Id == task.Id).Select(x => x.RowVersion).SingleAsync());
        }
        DateTimeOffset startAt = task.StartAt;

        Func<HttpClient, Guid, Task<HttpResponseMessage>>[] operations =
        [
            (client, id) => client.GetAsync($"/api/v1/projects/{id}/task-items"),
            (client, id) => client.PostAsJsonAsync($"/api/v1/projects/{id}/task-items", new
            {
                title = "Scope 建立",
                description = (string?)null,
                assignedAccountId = assigneeId,
                startAt,
                deadline = startAt.AddDays(1)
            }),
            (client, id) => client.GetAsync($"/api/v1/projects/{id}/task-items/{task.Id}"),
            (client, id) => client.PutAsJsonAsync($"/api/v1/projects/{id}/task-items/{task.Id}", new
            {
                title = "Scope 更新",
                description = (string?)null,
                assignedAccountId = assigneeId,
                startAt,
                deadline = startAt.AddDays(1),
                status = TaskStatus.Pending,
                rowVersion
            }),
            (client, id) => client.PatchAsJsonAsync($"/api/v1/projects/{id}/task-items/batch-status", new
            {
                tasks = new[] { new { taskId = task.Id, rowVersion } },
                targetStatus = TaskStatus.Pending
            }),
            (client, id) => client.DeleteAsync(
                $"/api/v1/projects/{id}/task-items/{task.Id}?rowVersion={Uri.EscapeDataString(rowVersion)}"),
            (client, id) => client.GetAsync($"/api/v1/projects/{id}/task-items/{task.Id}/comments"),
            (client, id) => client.PostAsJsonAsync(
                $"/api/v1/projects/{id}/task-items/{task.Id}/comments", new { content = "Scope 留言" }),
            (client, id) => client.PutAsJsonAsync(
                $"/api/v1/projects/{id}/task-items/{task.Id}/comments/{commentId}",
                new { content = "Scope 留言", rowVersion }),
            (client, id) => client.DeleteAsync(
                $"/api/v1/projects/{id}/task-items/{task.Id}/comments/{commentId}?rowVersion={Uri.EscapeDataString(rowVersion)}")
        ];

        using HttpClient outsider = await CreateAuthenticatedClientAsync(outsiderAccount);
        foreach (Func<HttpClient, Guid, Task<HttpResponseMessage>> operation in operations)
        {
            await AssertProblemAsync(await operation(outsider, project.Id), HttpStatusCode.Forbidden, "forbidden");
            await AssertProblemAsync(await operation(outsider, missingProjectId), HttpStatusCode.Forbidden, "forbidden");
        }
        foreach (Guid projectId in new[] { project.Id, missingProjectId })
        {
            await AssertProblemAsync(await outsider.PatchAsJsonAsync(
                $"/api/v1/projects/{projectId}/task-items/{task.Id}/status-and-deadline",
                new { status = TaskStatus.Pending, deadline = startAt.AddDays(1), rowVersion }),
                HttpStatusCode.Forbidden,
                "forbidden");
        }

        using HttpClient admin = await CreateAuthenticatedClientAsync(adminAccount);
        foreach (Func<HttpClient, Guid, Task<HttpResponseMessage>> operation in operations)
        {
            await AssertProblemAsync(await operation(admin, missingProjectId), HttpStatusCode.NotFound, "not_found");
        }

        using HttpClient assignee = await CreateAuthenticatedClientAsync(
            (await GetAccountAsync(assigneeId))!);
        await AssertProblemAsync(await assignee.PatchAsJsonAsync(
            $"/api/v1/projects/{project.Id}/task-items/{Guid.NewGuid()}/status-and-deadline",
            new { status = TaskStatus.Pending, deadline = startAt.AddDays(1), rowVersion }),
            HttpStatusCode.NotFound,
            "not_found");

        (await GetJsonAsync(admin, $"/api/v1/projects/{project.Id}/task-items"))
            .GetProperty("items").GetArrayLength().Should().Be(1);
        (await GetJsonAsync(admin, $"/api/v1/projects/{project.Id}/task-items/{task.Id}"))
            .GetProperty("id").GetGuid().Should().Be(task.Id);
    }

    // 測試案例：TC-F-TASK-008、TC-SQL-006（建立 Task、JWT actor、history 與 audit）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:47:48 +08:00
    [Test]
    public async Task 建立Task應固定Pending並產生編號History與Audit()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid assigneeId, _) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, [assigneeId]);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);
        DateTimeOffset startAt = DateTimeOffset.UtcNow.AddHours(1);

        HttpResponseMessage response = await owner.PostAsJsonAsync($"/api/v1/projects/{project.Id}/task-items", new
        {
            title = "  新增 Task  ",
            description = "  測試描述  ",
            assignedAccountId = assigneeId,
            startAt,
            deadline = startAt.AddDays(1)
        });
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        JsonElement created = JsonDocument.Parse(body).RootElement;
        Guid taskId = created.GetProperty("id").GetGuid();
        _taskIds.Add(taskId);
        created.GetProperty("title").GetString().Should().Be("新增 Task");
        created.GetProperty("description").GetString().Should().Be("測試描述");
        created.GetProperty("status").GetString().Should().Be(TaskStatus.Pending.ToString());
        TaskCodeRegex().IsMatch(created.GetProperty("code").GetString()!).Should().BeTrue();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        TaskItem persisted = await db.TaskItems.SingleAsync(x => x.Id == taskId);
        persisted.Status.Should().Be(TaskStatus.Pending);
        TaskItemHistory history = await db.TaskItemHistories.SingleAsync(x =>
            x.TaskItemId == taskId && x.Action == "Create");
        history.ActorAccountId.Should().Be(ownerId).And.NotBe(assigneeId);
        JsonDocument.Parse(history.Snapshot).RootElement.GetProperty("Title").GetString()
            .Should().Be("新增 Task");
        history.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        AuditLog audit = await db.AuditLogs.SingleAsync(x =>
            x.EntityType == "TaskItem" && x.EntityId == taskId.ToString() && x.Action == "Create");
        audit.ActorAccountId.Should().Be(ownerId).And.NotBe(assigneeId);
        audit.BeforeData.Should().BeNull();
        JsonDocument.Parse(audit.AfterData!).RootElement.GetProperty("Title").GetString()
            .Should().Be("新增 Task");
        audit.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    // 測試案例：TC-F-TASK-010、TC-F-TASK-011、TC-ERR-TASK-012、TC-SQL-006（完整更新、JWT actor 與失敗無成功追蹤）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:47:48 +08:00
    [Test]
    public async Task Task更新應依管理者被指派者非指派者與Viewer權限限制欄位()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid assigneeId, string assigneeAccount) = await CreateUserAsync(SystemRoles.User);
        (Guid nonAssigneeId, string nonAssigneeAccount) = await CreateUserAsync(SystemRoles.User);
        (Guid viewerId, string viewerAccount) = await CreateUserAsync(SystemRoles.Viewer);
        Project project = await CreateProjectAsync(ownerId, [assigneeId, nonAssigneeId, viewerId]);
        DateTimeOffset startAt = DateTimeOffset.UtcNow.AddHours(-2);
        TaskItem task = await CreateTaskAsync(project.Id, ownerId, assigneeId, "原標題", "原描述", startAt,
            startAt.AddDays(1), TaskStatus.Pending);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);
        JsonElement original = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{task.Id}");

        JsonElement updated = await PutJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{task.Id}", new
        {
            title = "完整更新標題",
            description = "完整更新描述",
            assignedAccountId = assigneeId,
            startAt,
            deadline = startAt.AddDays(3),
            status = TaskStatus.Blocked,
            rowVersion = original.GetProperty("rowVersion").GetString()
        });
        updated.GetProperty("title").GetString().Should().Be("完整更新標題");
        updated.GetProperty("status").GetString().Should().Be(TaskStatus.Blocked.ToString());
        updated.GetProperty("rowVersion").GetString().Should().NotBe(original.GetProperty("rowVersion").GetString());

        using HttpClient assignee = await CreateAuthenticatedClientAsync(assigneeAccount);
        HttpResponseMessage patchResponse = await assignee.PatchAsJsonAsync(
            $"/api/v1/projects/{project.Id}/task-items/{task.Id}/status-and-deadline", new
            {
                status = TaskStatus.Completed,
                deadline = startAt.AddDays(4),
                rowVersion = updated.GetProperty("rowVersion").GetString()
            });
        string patchBody = await patchResponse.Content.ReadAsStringAsync();
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK, patchBody);
        JsonElement patched = JsonDocument.Parse(patchBody).RootElement;
        patched.GetProperty("status").GetString().Should().Be(TaskStatus.Completed.ToString());
        patched.GetProperty("title").GetString().Should().Be("完整更新標題");

        await AssertProblemAsync(await assignee.PutAsJsonAsync($"/api/v1/projects/{project.Id}/task-items/{task.Id}", new
        {
            title = "不得竄改",
            description = "不得竄改",
            assignedAccountId = nonAssigneeId,
            startAt,
            deadline = startAt.AddDays(5),
            status = TaskStatus.Pending,
            rowVersion = patched.GetProperty("rowVersion").GetString()
        }), HttpStatusCode.Forbidden, "forbidden");

        foreach (string account in new[] { nonAssigneeAccount, viewerAccount })
        {
            using HttpClient denied = await CreateAuthenticatedClientAsync(account);
            await AssertProblemAsync(await denied.PostAsJsonAsync($"/api/v1/projects/{project.Id}/task-items", new
            {
                title = "無權建立",
                description = (string?)null,
                assignedAccountId = assigneeId,
                startAt,
                deadline = startAt.AddDays(1)
            }), HttpStatusCode.Forbidden, "forbidden");
            await AssertProblemAsync(await denied.PutAsJsonAsync($"/api/v1/projects/{project.Id}/task-items/{task.Id}", new
            {
                title = "無權更新",
                description = (string?)null,
                assignedAccountId = assigneeId,
                startAt,
                deadline = startAt.AddDays(1),
                status = TaskStatus.Pending,
                rowVersion = patched.GetProperty("rowVersion").GetString()
            }), HttpStatusCode.Forbidden, "forbidden");
            await AssertProblemAsync(await denied.PatchAsJsonAsync(
                $"/api/v1/projects/{project.Id}/task-items/{task.Id}/status-and-deadline", new
                {
                    status = TaskStatus.Pending,
                    deadline = startAt.AddDays(1),
                    rowVersion = patched.GetProperty("rowVersion").GetString()
                }), HttpStatusCode.Forbidden, "forbidden");
            await AssertProblemAsync(await denied.PatchAsJsonAsync($"/api/v1/projects/{project.Id}/task-items/batch-status", new
            {
                tasks = new[] { new { taskId = task.Id, rowVersion = patched.GetProperty("rowVersion").GetString() } },
                targetStatus = TaskStatus.Pending
            }), HttpStatusCode.Forbidden, "forbidden");
            await AssertProblemAsync(await denied.DeleteAsync(
                $"/api/v1/projects/{project.Id}/task-items/{task.Id}?rowVersion={Uri.EscapeDataString(patched.GetProperty("rowVersion").GetString()!)}"),
                HttpStatusCode.Forbidden, "forbidden");
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        TaskItem persisted = await db.TaskItems.SingleAsync(x => x.Id == task.Id);
        persisted.Title.Should().Be("完整更新標題");
        persisted.Status.Should().Be(TaskStatus.Completed);
        TaskItemHistory updateHistory = await db.TaskItemHistories.SingleAsync(x =>
            x.TaskItemId == task.Id && x.Action == "Update");
        updateHistory.ActorAccountId.Should().Be(ownerId);
        JsonDocument.Parse(updateHistory.Snapshot).RootElement.GetProperty("Title").GetString()
            .Should().Be("完整更新標題");
        TaskItemHistory assignedHistory = await db.TaskItemHistories.SingleAsync(x =>
            x.TaskItemId == task.Id && x.Action == "UpdateAssigned");
        assignedHistory.ActorAccountId.Should().Be(assigneeId);
        AuditLog assignedAudit = await db.AuditLogs.SingleAsync(x =>
            x.EntityId == task.Id.ToString() && x.Action == "UpdateAssigned");
        assignedAudit.ActorAccountId.Should().Be(assigneeId);
        assignedAudit.BeforeData.Should().NotBeNull();
        assignedAudit.AfterData.Should().NotBeNull();
    }

    // 測試案例：TC-ST-TASK-013（完整修改、被指派者修改與批次修改各自覆蓋四種狀態 16 組 API 轉換）
    // 測試結果：Passed（每種 API 皆為 16/16 狀態組合）
    // 上次測試時間：2026-09-16 10:05:02 +08:00
    [Test]
    public async Task Task四種狀態應允許三種Api任意互轉並產生追蹤紀錄()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid assigneeId, string assigneeAccount) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, [assigneeId]);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);
        using HttpClient assignee = await CreateAuthenticatedClientAsync(assigneeAccount);
        var fullUpdateIds = new List<Guid>();
        var assignedUpdateIds = new List<Guid>();
        var batchUpdateIds = new List<Guid>();

        foreach (TaskStatus source in Enum.GetValues<TaskStatus>())
        {
            foreach (TaskStatus target in Enum.GetValues<TaskStatus>())
            {
                DateTimeOffset startAt = DateTimeOffset.UtcNow.AddHours(-1);
                TaskItem fullTask = await CreateTaskAsync(project.Id, ownerId, assigneeId,
                    $"完整-{source}-{target}", null,
                    startAt, startAt.AddDays(1), source);
                TaskItem assignedTask = await CreateTaskAsync(project.Id, ownerId, assigneeId,
                    $"指派-{source}-{target}", null,
                    startAt, startAt.AddDays(1), source);
                TaskItem batchTask = await CreateTaskAsync(project.Id, ownerId, assigneeId,
                    $"批次-{source}-{target}", null,
                    startAt, startAt.AddDays(1), source);
                fullUpdateIds.Add(fullTask.Id);
                assignedUpdateIds.Add(assignedTask.Id);
                batchUpdateIds.Add(batchTask.Id);

                JsonElement fullCurrent = await GetJsonAsync(
                    owner, $"/api/v1/projects/{project.Id}/task-items/{fullTask.Id}");
                JsonElement fullUpdated = await PutJsonAsync(
                    owner, $"/api/v1/projects/{project.Id}/task-items/{fullTask.Id}", new
                    {
                        title = $"完整更新-{source}-{target}",
                        description = (string?)null,
                        assignedAccountId = assigneeId,
                        startAt,
                        deadline = startAt.AddDays(2),
                        status = target,
                        rowVersion = fullCurrent.GetProperty("rowVersion").GetString()
                    });
                fullUpdated.GetProperty("status").GetString().Should().Be(target.ToString());

                JsonElement assignedCurrent = await GetJsonAsync(
                    assignee, $"/api/v1/projects/{project.Id}/task-items/{assignedTask.Id}");
                HttpResponseMessage assignedResponse = await assignee.PatchAsJsonAsync(
                    $"/api/v1/projects/{project.Id}/task-items/{assignedTask.Id}/status-and-deadline", new
                    {
                        status = target,
                        deadline = startAt.AddDays(2),
                        rowVersion = assignedCurrent.GetProperty("rowVersion").GetString()
                    });
                string assignedBody = await assignedResponse.Content.ReadAsStringAsync();
                assignedResponse.StatusCode.Should().Be(
                    HttpStatusCode.OK, $"被指派者 {source} -> {target}: {assignedBody}");
                JsonDocument.Parse(assignedBody).RootElement.GetProperty("status").GetString()
                    .Should().Be(target.ToString());

                JsonElement batchCurrent = await GetJsonAsync(
                    owner, $"/api/v1/projects/{project.Id}/task-items/{batchTask.Id}");
                HttpResponseMessage batchResponse = await owner.PatchAsJsonAsync(
                    $"/api/v1/projects/{project.Id}/task-items/batch-status", new
                    {
                        tasks = new[]
                        {
                            new
                            {
                                taskId = batchTask.Id,
                                rowVersion = batchCurrent.GetProperty("rowVersion").GetString()
                            }
                        },
                        targetStatus = target
                    });
                string batchBody = await batchResponse.Content.ReadAsStringAsync();
                batchResponse.StatusCode.Should().Be(
                    HttpStatusCode.OK, $"批次 {source} -> {target}: {batchBody}");
                JsonElement batchUpdated = await GetJsonAsync(
                    owner, $"/api/v1/projects/{project.Id}/task-items/{batchTask.Id}");
                batchUpdated.GetProperty("status").GetString().Should().Be(target.ToString());
            }
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.TaskItemHistories.CountAsync(x => fullUpdateIds.Contains(x.TaskItemId) && x.Action == "Update"))
            .Should().Be(16);
        (await db.TaskItemHistories.CountAsync(x => assignedUpdateIds.Contains(x.TaskItemId) &&
                                                     x.Action == "UpdateAssigned"))
            .Should().Be(16);
        (await db.TaskItemHistories.CountAsync(x => batchUpdateIds.Contains(x.TaskItemId) &&
                                                     x.Action == "BatchUpdateStatus"))
            .Should().Be(16);
        (await db.AuditLogs.CountAsync(x => x.EntityType == "TaskItem" && x.Action == "Update" &&
                                             fullUpdateIds.Select(id => id.ToString()).Contains(x.EntityId)))
            .Should().Be(16);
        (await db.AuditLogs.CountAsync(x => x.EntityType == "TaskItem" && x.Action == "UpdateAssigned" &&
                                             assignedUpdateIds.Select(id => id.ToString()).Contains(x.EntityId)))
            .Should().Be(16);
        (await db.AuditLogs.CountAsync(x => x.EntityType == "TaskItem" && x.Action == "BatchUpdateStatus" &&
                                             batchUpdateIds.Select(id => id.ToString()).Contains(x.EntityId)))
            .Should().Be(16);
    }

    // 測試案例：TC-ERR-TASK-014、TC-SQL-005（stale／空白／非法 Base64 rowVersion）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Task舊RowVersion的三種異動皆應衝突且不得覆蓋新資料()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid assigneeId, string assigneeAccount) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, [assigneeId]);
        DateTimeOffset startAt = DateTimeOffset.UtcNow;
        TaskItem task = await CreateTaskAsync(project.Id, ownerId, assigneeId, "版本原值", null, startAt,
            startAt.AddDays(1), TaskStatus.Pending);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);
        using HttpClient assignee = await CreateAuthenticatedClientAsync(assigneeAccount);
        JsonElement original = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{task.Id}");
        string staleVersion = original.GetProperty("rowVersion").GetString()!;
        JsonElement winner = await PutJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{task.Id}", new
        {
            title = "勝出版本",
            description = "只保留這次",
            assignedAccountId = assigneeId,
            startAt,
            deadline = startAt.AddDays(2),
            status = TaskStatus.Blocked,
            rowVersion = staleVersion
        });

        await AssertProblemAsync(await owner.PutAsJsonAsync($"/api/v1/projects/{project.Id}/task-items/{task.Id}", new
        {
            title = "失敗版本",
            description = (string?)null,
            assignedAccountId = assigneeId,
            startAt,
            deadline = startAt.AddDays(3),
            status = TaskStatus.Completed,
            rowVersion = staleVersion
        }), HttpStatusCode.Conflict, "concurrency_conflict");
        await AssertProblemAsync(await assignee.PatchAsJsonAsync(
            $"/api/v1/projects/{project.Id}/task-items/{task.Id}/status-and-deadline", new
            {
                status = TaskStatus.Completed,
                deadline = startAt.AddDays(3),
                rowVersion = staleVersion
            }), HttpStatusCode.Conflict, "concurrency_conflict");
        await AssertProblemAsync(await owner.DeleteAsync(
            $"/api/v1/projects/{project.Id}/task-items/{task.Id}?rowVersion={Uri.EscapeDataString(staleVersion)}"),
            HttpStatusCode.Conflict, "concurrency_conflict");
        foreach (string invalidRowVersion in new[] { "", "   ", "not-base64" })
        {
            await AssertProblemAsync(await owner.PutAsJsonAsync(
                $"/api/v1/projects/{project.Id}/task-items/{task.Id}", new
                {
                    title = "非法版本不得更新",
                    description = "不得保存",
                    assignedAccountId = assigneeId,
                    startAt,
                    deadline = startAt.AddDays(3),
                    status = TaskStatus.Completed,
                    rowVersion = invalidRowVersion
                }), HttpStatusCode.Conflict, "concurrency_conflict");
        }

        JsonElement persisted = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{task.Id}");
        persisted.GetProperty("title").GetString().Should().Be("勝出版本");
        persisted.GetProperty("status").GetString().Should().Be(TaskStatus.Blocked.ToString());
        persisted.GetProperty("rowVersion").GetString().Should().Be(winner.GetProperty("rowVersion").GetString());
    }

    // 測試案例：TC-F-TASK-019（軟刪除與 Comment／History 關聯保留）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 刪除Task應軟刪除並保留CommentHistory與Audit()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        Project project = await CreateProjectAsync(ownerId, []);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TaskItem task = await CreateTaskAsync(project.Id, ownerId, ownerId, "軟刪除目標", null, now, now.AddDays(1),
            TaskStatus.Pending);
        Guid commentId = Guid.NewGuid();
        _commentIds.Add(commentId);
        await using (AsyncServiceScope setupScope = _factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext setupDb = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            setupDb.TaskItemComments.Add(new TaskItemComment(commentId, task.Id, ownerId, "保留留言", now));
            setupDb.TaskItemHistories.Add(new TaskItemHistory(Guid.NewGuid(), task.Id, ownerId, "Existing", "{}", now));
            await setupDb.SaveChangesAsync();
        }
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);
        JsonElement current = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{task.Id}");

        HttpResponseMessage response = await owner.DeleteAsync(
            $"/api/v1/projects/{project.Id}/task-items/{task.Id}?rowVersion={Uri.EscapeDataString(current.GetProperty("rowVersion").GetString()!)}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        JsonElement list = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items?pageSize=100");
        list.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Should().NotContain(task.Id);
        await AssertProblemAsync(await owner.GetAsync($"/api/v1/projects/{project.Id}/task-items/{task.Id}"),
            HttpStatusCode.NotFound, "not_found");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        TaskItem deleted = await db.TaskItems.IgnoreQueryFilters().SingleAsync(x => x.Id == task.Id);
        deleted.DeletedAt.Should().NotBeNull();
        (await db.TaskItemComments.IgnoreQueryFilters().CountAsync(x => x.TaskItemId == task.Id)).Should().Be(1);
        (await db.TaskItemHistories.CountAsync(x => x.TaskItemId == task.Id && x.Action == "Existing")).Should().Be(1);
        (await db.TaskItemHistories.CountAsync(x => x.TaskItemId == task.Id && x.Action == "Delete")).Should().Be(1);
        (await db.AuditLogs.CountAsync(x => x.EntityId == task.Id.ToString() && x.Action == "Delete")).Should().Be(1);
    }

    // 測試案例：TC-ERR-TASK-009、TC-ERR-TASK-022（建立／修改的欄位、指派者與日期邊界）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Task建立與修改應套用相同欄位指派者及日期驗證且失敗不留追蹤()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid assigneeId, string assigneeAccount) = await CreateUserAsync(SystemRoles.User);
        (Guid otherProjectMemberId, _) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, [assigneeId]);
        await CreateProjectAsync(ownerId, [otherProjectMemberId]);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);
        DateTimeOffset startAt = DateTimeOffset.UtcNow.AddHours(1);

        var invalidCreateRequests = new (object Request, HttpStatusCode Status, string Code)[]
        {
            (new { title = " ", description = (string?)null, assignedAccountId = assigneeId, startAt, deadline = startAt.AddDays(1) },
                HttpStatusCode.BadRequest, "validation_error"),
            (new { title = new string('標', 301), description = (string?)null, assignedAccountId = assigneeId, startAt, deadline = startAt.AddDays(1) },
                HttpStatusCode.BadRequest, "validation_error"),
            (new { title = "不存在指派者", description = (string?)null, assignedAccountId = Guid.NewGuid(), startAt, deadline = startAt.AddDays(1) },
                HttpStatusCode.UnprocessableEntity, "invalid_assignee"),
            (new { title = "跨專案指派者", description = (string?)null, assignedAccountId = otherProjectMemberId, startAt, deadline = startAt.AddDays(1) },
                HttpStatusCode.UnprocessableEntity, "invalid_assignee"),
            (new { title = "日期錯誤", description = (string?)null, assignedAccountId = assigneeId, startAt, deadline = startAt.AddMinutes(-1) },
                HttpStatusCode.UnprocessableEntity, "invalid_deadline"),
            (new { title = "描述過長", description = new string('說', 8001), assignedAccountId = assigneeId, startAt, deadline = startAt.AddDays(1) },
                HttpStatusCode.BadRequest, "validation_error")
        };
        foreach ((object request, HttpStatusCode status, string code) in invalidCreateRequests)
        {
            await AssertProblemAsync(await owner.PostAsJsonAsync($"/api/v1/projects/{project.Id}/task-items", request), status, code);
        }

        foreach (string? description in new string?[] { null, "   ", new string('說', 8000) })
        {
            HttpResponseMessage response = await owner.PostAsJsonAsync($"/api/v1/projects/{project.Id}/task-items", new
            {
                title = "合法描述邊界",
                description,
                assignedAccountId = assigneeId,
                startAt,
                deadline = startAt.AddDays(1)
            });
            string body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Created, body);
            _taskIds.Add(JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid());
        }

        TaskItem target = await CreateTaskAsync(project.Id, ownerId, assigneeId, "修改前標題", "修改前描述", startAt,
            startAt.AddDays(1), TaskStatus.Pending);
        JsonElement current = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{target.Id}");
        string version = current.GetProperty("rowVersion").GetString()!;
        var invalidUpdateRequests = new (object Request, HttpStatusCode Status, string Code)[]
        {
            (new { title = " ", description = "原值", assignedAccountId = assigneeId, startAt, deadline = startAt.AddDays(1), status = TaskStatus.Pending, rowVersion = version },
                HttpStatusCode.BadRequest, "validation_error"),
            (new { title = new string('標', 301), description = "原值", assignedAccountId = assigneeId, startAt, deadline = startAt.AddDays(1), status = TaskStatus.Pending, rowVersion = version },
                HttpStatusCode.BadRequest, "validation_error"),
            (new { title = "描述過長", description = new string('說', 8001), assignedAccountId = assigneeId, startAt, deadline = startAt.AddDays(1), status = TaskStatus.Pending, rowVersion = version },
                HttpStatusCode.BadRequest, "validation_error"),
            (new { title = "指派錯誤", description = "原值", assignedAccountId = otherProjectMemberId, startAt, deadline = startAt.AddDays(1), status = TaskStatus.Pending, rowVersion = version },
                HttpStatusCode.UnprocessableEntity, "invalid_assignee"),
            (new { title = "日期錯誤", description = "原值", assignedAccountId = assigneeId, startAt, deadline = startAt.AddMinutes(-1), status = TaskStatus.Pending, rowVersion = version },
                HttpStatusCode.UnprocessableEntity, "invalid_deadline")
        };
        foreach ((object request, HttpStatusCode status, string code) in invalidUpdateRequests)
        {
            await AssertProblemAsync(await owner.PutAsJsonAsync($"/api/v1/projects/{project.Id}/task-items/{target.Id}", request), status, code);
        }

        using HttpClient assignee = await CreateAuthenticatedClientAsync(assigneeAccount);
        HttpResponseMessage equalDeadline = await assignee.PatchAsJsonAsync(
            $"/api/v1/projects/{project.Id}/task-items/{target.Id}/status-and-deadline", new
            {
                status = TaskStatus.InProgress,
                deadline = startAt,
                rowVersion = version
            });
        string equalBody = await equalDeadline.Content.ReadAsStringAsync();
        equalDeadline.StatusCode.Should().Be(HttpStatusCode.OK, equalBody);
        string newVersion = JsonDocument.Parse(equalBody).RootElement.GetProperty("rowVersion").GetString()!;
        await AssertProblemAsync(await assignee.PatchAsJsonAsync(
            $"/api/v1/projects/{project.Id}/task-items/{target.Id}/status-and-deadline", new
            {
                status = TaskStatus.Completed,
                deadline = startAt.AddTicks(-1),
                rowVersion = newVersion
            }), HttpStatusCode.UnprocessableEntity, "invalid_deadline");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        TaskItem persisted = await db.TaskItems.SingleAsync(x => x.Id == target.Id);
        persisted.Title.Should().Be("修改前標題");
        persisted.Description.Should().Be("修改前描述");
        persisted.Status.Should().Be(TaskStatus.InProgress);
        persisted.Deadline.Should().Be(startAt);
        (await db.TaskItemHistories.CountAsync(x => x.TaskItemId == target.Id)).Should().Be(1);
        (await db.AuditLogs.CountAsync(x => x.EntityId == target.Id.ToString())).Should().Be(1);
    }

    // 測試案例：TC-F-TASK-015、TC-ERR-TASK-016、TC-ERR-TASK-017（批次 1／10／11、優先序與全有全無）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 批次狀態更新應限制十筆並依權限不存在與版本衝突全批Rollback()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid userId, string userAccount) = await CreateUserAsync(SystemRoles.User);
        (Guid otherId, _) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, [userId, otherId]);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var tasks = new List<TaskItem>();
        for (int index = 0; index < 13; index++)
        {
            tasks.Add(await CreateTaskAsync(project.Id, ownerId, index == 12 ? otherId : userId,
                $"批次-{index}", null, now.AddMilliseconds(index), now.AddDays(1), TaskStatus.Pending));
        }
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);

        JsonElement first = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{tasks[0].Id}");
        JsonElement oneResult = await PatchJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/batch-status", new
        {
            tasks = new[] { new { taskId = tasks[0].Id, rowVersion = first.GetProperty("rowVersion").GetString() } },
            targetStatus = TaskStatus.InProgress
        });
        oneResult.GetProperty("updatedCount").GetInt32().Should().Be(1);

        var tenItems = new List<object>();
        for (int index = 1; index <= 10; index++)
        {
            JsonElement item = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{tasks[index].Id}");
            tenItems.Add(new { taskId = tasks[index].Id, rowVersion = item.GetProperty("rowVersion").GetString() });
        }
        JsonElement tenResult = await PatchJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/batch-status", new
        {
            tasks = tenItems,
            targetStatus = TaskStatus.Blocked
        });
        tenResult.GetProperty("updatedCount").GetInt32().Should().Be(10);

        await AssertProblemAsync(await owner.PatchAsJsonAsync($"/api/v1/projects/{project.Id}/task-items/batch-status", new
        {
            tasks = Array.Empty<object>(),
            targetStatus = TaskStatus.Completed
        }), HttpStatusCode.BadRequest, "validation_error");
        JsonElement duplicate = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{tasks[11].Id}");
        await AssertProblemAsync(await owner.PatchAsJsonAsync($"/api/v1/projects/{project.Id}/task-items/batch-status", new
        {
            tasks = new[]
            {
                new { taskId = tasks[11].Id, rowVersion = duplicate.GetProperty("rowVersion").GetString()! },
                new { taskId = tasks[11].Id, rowVersion = "different" }
            },
            targetStatus = TaskStatus.Completed
        }), HttpStatusCode.BadRequest, "duplicate_task");

        var elevenItems = new List<object>();
        for (int index = 0; index < 11; index++)
        {
            JsonElement item = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{tasks[index].Id}");
            elevenItems.Add(new { taskId = tasks[index].Id, rowVersion = item.GetProperty("rowVersion").GetString() });
        }
        await AssertProblemAsync(await owner.PatchAsJsonAsync($"/api/v1/projects/{project.Id}/task-items/batch-status", new
        {
            tasks = elevenItems,
            targetStatus = TaskStatus.Completed
        }), HttpStatusCode.BadRequest, "validation_error");

        using HttpClient user = await CreateAuthenticatedClientAsync(userAccount);
        JsonElement own = await GetJsonAsync(user, $"/api/v1/projects/{project.Id}/task-items/{tasks[11].Id}");
        JsonElement unauthorized = await GetJsonAsync(user, $"/api/v1/projects/{project.Id}/task-items/{tasks[12].Id}");
        await AssertProblemAsync(await user.PatchAsJsonAsync($"/api/v1/projects/{project.Id}/task-items/batch-status", new
        {
            tasks = new object[]
            {
                new { taskId = tasks[11].Id, rowVersion = own.GetProperty("rowVersion").GetString() },
                new { taskId = tasks[12].Id, rowVersion = unauthorized.GetProperty("rowVersion").GetString() },
                new { taskId = Guid.NewGuid(), rowVersion = "missing" }
            },
            targetStatus = TaskStatus.Completed
        }), HttpStatusCode.Forbidden, "forbidden");
        await AssertProblemAsync(await owner.PatchAsJsonAsync($"/api/v1/projects/{project.Id}/task-items/batch-status", new
        {
            tasks = new[] { new { taskId = Guid.NewGuid(), rowVersion = "missing" } },
            targetStatus = TaskStatus.Completed
        }), HttpStatusCode.NotFound, "not_found");

        JsonElement staleA = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{tasks[11].Id}");
        JsonElement staleB = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{tasks[12].Id}");
        JsonElement winner = await PutJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{tasks[12].Id}", new
        {
            title = "並行勝出",
            description = (string?)null,
            assignedAccountId = otherId,
            startAt = now.AddMilliseconds(12),
            deadline = now.AddDays(2),
            status = TaskStatus.InProgress,
            rowVersion = staleB.GetProperty("rowVersion").GetString()
        });
        await AssertProblemAsync(await owner.PatchAsJsonAsync($"/api/v1/projects/{project.Id}/task-items/batch-status", new
        {
            tasks = new[]
            {
                new { taskId = tasks[11].Id, rowVersion = staleA.GetProperty("rowVersion").GetString() },
                new { taskId = tasks[12].Id, rowVersion = staleB.GetProperty("rowVersion").GetString() }
            },
            targetStatus = TaskStatus.Completed
        }), HttpStatusCode.Conflict, "concurrency_conflict");

        JsonElement unchangedA = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{tasks[11].Id}");
        JsonElement unchangedB = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}/task-items/{tasks[12].Id}");
        unchangedA.GetProperty("status").GetString().Should().Be(TaskStatus.Pending.ToString());
        unchangedB.GetProperty("rowVersion").GetString().Should().Be(winner.GetProperty("rowVersion").GetString());

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.TaskItemHistories.CountAsync(x => tasks.Take(11).Select(t => t.Id).Contains(x.TaskItemId) &&
                                                      x.Action == "BatchUpdateStatus")).Should().Be(11);
        (await db.AuditLogs.CountAsync(x => x.Action == "BatchUpdateStatus" &&
                                             tasks.Take(11).Select(t => t.Id.ToString()).Contains(x.EntityId))).Should().Be(11);
    }

    private async Task<(Guid Id, string Account)> CreateUserAsync(string role)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string account = $"task-api-{role.ToLowerInvariant()}-{suffix}";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = account,
            Email = $"{account}@example.test",
            Name = $"{role} Task 測試帳號",
            EmailConfirmed = true,
            IsEnabled = true
        };
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await userManager.CreateAsync(user, ValidPassword)).Succeeded.Should().BeTrue();
        (await userManager.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        db.UserPreferences.Add(new UserPreference(user.Id));
        await db.SaveChangesAsync();
        _accountIds.Add(user.Id);
        return (user.Id, account);
    }

    private async Task<string?> GetAccountAsync(Guid accountId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users
            .Where(x => x.Id == accountId).Select(x => x.UserName).SingleAsync();
    }

    private async Task<Project> CreateProjectAsync(Guid ownerId, IReadOnlyCollection<Guid> members)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new Project(Guid.NewGuid(), $"PRJ-T{Guid.NewGuid():N}"[..20], $"Task API {Guid.NewGuid():N}", null, ownerId, "Asia/Taipei", now);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Guid managerRoleId = await db.ProjectRoles.Where(x => x.Code == ProjectRoleCodes.ProjectManager)
            .Select(x => x.Id).SingleAsync();
        db.Projects.Add(project);
        foreach (Guid accountId in members.Append(ownerId).Distinct())
        {
            db.ProjectMembers.Add(new ProjectMember(project.Id, accountId, now));
            db.ProjectMemberRoles.Add(new ProjectMemberRole(project.Id, accountId, managerRoleId));
        }
        await db.SaveChangesAsync();
        _projectIds.Add(project.Id);
        return project;
    }

    private async Task<TaskItem> CreateTaskAsync(Guid projectId, Guid creatorId, Guid assigneeId, string title,
        string? description, DateTimeOffset createdAt, DateTimeOffset deadline, TaskStatus status, bool deleted = false)
    {
        var task = new TaskItem(Guid.NewGuid(), $"TASK-T{Guid.NewGuid():N}"[..22], projectId, creatorId, assigneeId,
            title, description, createdAt, deadline, createdAt);
        if (status != TaskStatus.Pending)
        {
            task.UpdateStatus(status, createdAt.AddMilliseconds(1));
        }
        if (deleted)
        {
            task.SoftDelete(createdAt.AddMilliseconds(2));
        }
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.TaskItems.Add(task);
        await db.SaveChangesAsync();
        _taskIds.Add(task.Id);
        return task;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string account)
    {
        HttpClient client = _factory.CreateClient();
        JsonElement csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/security/csrf-token");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { account, password = ValidPassword })
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", JsonDocument.Parse(body).RootElement.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> PutJsonAsync(HttpClient client, string path, object request)
    {
        HttpResponseMessage response = await client.PutAsJsonAsync(path, request);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> PatchJsonAsync(HttpClient client, string path, object request)
    {
        HttpResponseMessage response = await client.PatchAsJsonAsync(path, request);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, body);
        JsonElement problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be(code);
        problem.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        body.Should().NotContain("System.").And.NotContain("Microsoft.Data.SqlClient").And.NotContain("accessToken");
    }

    private async Task CleanupTestDataAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (_taskIds.Count > 0)
        {
            string[] entityIds = _taskIds.Select(x => x.ToString()).ToArray();
            await db.AuditLogs.Where(x => x.EntityType == "TaskItem" && entityIds.Contains(x.EntityId)).ExecuteDeleteAsync();
            await db.TaskItemComments.IgnoreQueryFilters().Where(x => _taskIds.Contains(x.TaskItemId)).ExecuteDeleteAsync();
            await db.TaskItemHistories.Where(x => _taskIds.Contains(x.TaskItemId)).ExecuteDeleteAsync();
            await db.TaskItems.IgnoreQueryFilters().Where(x => _taskIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
        if (_projectIds.Count > 0)
        {
            await db.ProjectMemberRoles.Where(x => _projectIds.Contains(x.ProjectId)).ExecuteDeleteAsync();
            await db.ProjectMembers.Where(x => _projectIds.Contains(x.ProjectId)).ExecuteDeleteAsync();
            await db.Projects.IgnoreQueryFilters().Where(x => _projectIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
        if (_accountIds.Count > 0)
        {
            await db.RefreshTokens.Where(x => _accountIds.Contains(x.AccountId)).ExecuteDeleteAsync();
            await db.Set<ApplicationUserRole>().Where(x => _accountIds.Contains(x.UserId)).ExecuteDeleteAsync();
            await db.UserPreferences.Where(x => _accountIds.Contains(x.AccountId)).ExecuteDeleteAsync();
            await db.Users.Where(x => _accountIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }

    [GeneratedRegex("^TASK-[0-9]{8}[0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex TaskCodeRegex();
}
