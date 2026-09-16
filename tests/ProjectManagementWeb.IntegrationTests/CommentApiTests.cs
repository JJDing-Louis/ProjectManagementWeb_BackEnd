using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.IntegrationTests;

[NonParallelizable]
public sealed class CommentApiTests
{
    private const string ValidPassword = "Test_password123!";
    private readonly HashSet<Guid> _accountIds = [];
    private readonly HashSet<Guid> _projectIds = [];
    private readonly HashSet<Guid> _taskIds = [];
    private readonly HashSet<Guid> _commentIds = [];
    private FailNextCommentSaveInterceptor _failureInterceptor = null!;
    private TestWebApplicationFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        string? connectionString = Environment.GetEnvironmentVariable("PMW_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Ignore("未設定 PMW_TEST_SQL_CONNECTION，略過實際 SQL Server Comment API 測試。");
        }
        _failureInterceptor = new FailNextCommentSaveInterceptor();
        _factory = new TestWebApplicationFactory(connectionString, saveChangesInterceptor: _failureInterceptor);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_factory is null) return;
        await CleanupTestDataAsync();
        await _factory.DisposeAsync();
    }

    // 測試案例：TC-F-CMT-001（升冪、軟刪除排除與 Viewer 唯讀）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 留言串應只回未刪除資料依建立時間升冪且Viewer可讀()
    {
        (Guid ownerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid viewerId, string viewerAccount) = await CreateUserAsync(SystemRoles.Viewer);
        Project project = await CreateProjectAsync(ownerId, [viewerId]);
        TaskItem task = await CreateTaskAsync(project.Id, ownerId, viewerId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TaskItemComment first = await CreateCommentAsync(task.Id, ownerId, "第一則", now.AddMinutes(-2));
        TaskItemComment second = await CreateCommentAsync(task.Id, viewerId, "第二則", now.AddMinutes(-1));
        await CreateCommentAsync(task.Id, ownerId, "已刪除", now, deleted: true);
        using HttpClient viewer = await CreateAuthenticatedClientAsync(viewerAccount);

        JsonElement result = await GetJsonAsync(viewer,
            $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments");
        result.GetArrayLength().Should().Be(2);
        result.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Should().Equal(first.Id, second.Id);
        JsonElement row = result[0];
        row.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(
            "id", "taskItemId", "authorAccountId", "content", "createdAt", "updatedAt", "rowVersion");
    }

    // 測試案例：TC-F-CMT-002、TC-ERR-CMT-003（建立／編輯 1、2000、空白與 2001 字）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 留言建立與編輯應Trim保留換行並套用一至兩千字邊界()
    {
        (Guid ownerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid authorId, string authorAccount) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, [authorId]);
        TaskItem task = await CreateTaskAsync(project.Id, ownerId, authorId);
        using HttpClient author = await CreateAuthenticatedClientAsync(authorAccount);

        foreach (string content in new[] { "字", new string('留', 2000), "  第一行\n第二行  " })
        {
            JsonElement created = await PostJsonAsync(author,
                $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments", new { content });
            _commentIds.Add(created.GetProperty("id").GetGuid());
            created.GetProperty("authorAccountId").GetGuid().Should().Be(authorId);
            created.GetProperty("content").GetString().Should().Be(content.Trim());
        }

        foreach (string content in new[] { "", "   ", new string('留', 2001) })
        {
            await AssertProblemAsync(await author.PostAsJsonAsync(
                $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments", new { content }),
                HttpStatusCode.BadRequest, "validation_error");
        }

        JsonElement comments = await GetJsonAsync(author,
            $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments");
        JsonElement target = comments[0];
        Guid targetId = target.GetProperty("id").GetGuid();
        foreach (string content in new[] { "", "   ", new string('留', 2001) })
        {
            await AssertProblemAsync(await author.PutAsJsonAsync(
                $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments/{targetId}", new
                {
                    content,
                    rowVersion = target.GetProperty("rowVersion").GetString()
                }), HttpStatusCode.BadRequest, "validation_error");
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.TaskItemComments.CountAsync(x => x.TaskItemId == task.Id)).Should().Be(3);
        (await db.AuditLogs.CountAsync(x => x.EntityType == "TaskItemComment" && x.Action == "Create" &&
                                             _commentIds.Select(id => id.ToString()).Contains(x.EntityId))).Should().Be(3);
    }

    // 測試案例：TC-ST-CMT-004、TC-ERR-CMT-005、TC-SQL-006（作者更新、JWT actor 與拒絕路徑無成功 audit）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:47:48 +08:00
    [Test]
    public async Task 只有留言作者可修改刪除且更新應產生新版本與Audit()
    {
        (Guid ownerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid authorId, string authorAccount) = await CreateUserAsync(SystemRoles.User);
        (Guid otherId, string otherAccount) = await CreateUserAsync(SystemRoles.User);
        (Guid viewerId, string viewerAccount) = await CreateUserAsync(SystemRoles.Viewer);
        Project project = await CreateProjectAsync(ownerId, [authorId, otherId, viewerId]);
        TaskItem task = await CreateTaskAsync(project.Id, ownerId, authorId);
        TaskItemComment comment = await CreateCommentAsync(task.Id, authorId, "原內容", DateTimeOffset.UtcNow);
        using HttpClient author = await CreateAuthenticatedClientAsync(authorAccount);
        JsonElement current = (await GetJsonAsync(author,
            $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments"))[0];

        JsonElement updated = await PutJsonAsync(author,
            $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments/{comment.Id}", new
            {
                content = "  更新內容\n第二行  ",
                rowVersion = current.GetProperty("rowVersion").GetString()
            });
        updated.GetProperty("content").GetString().Should().Be("更新內容\n第二行");
        updated.GetProperty("updatedAt").GetDateTimeOffset().Should().BeAfter(current.GetProperty("updatedAt").GetDateTimeOffset());
        updated.GetProperty("rowVersion").GetString().Should().NotBe(current.GetProperty("rowVersion").GetString());

        foreach (string account in new[] { otherAccount, viewerAccount })
        {
            using HttpClient denied = await CreateAuthenticatedClientAsync(account);
            await AssertProblemAsync(await denied.PutAsJsonAsync(
                $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments/{comment.Id}", new
                {
                    content = "不得修改",
                    rowVersion = updated.GetProperty("rowVersion").GetString()
                }), HttpStatusCode.Forbidden, "forbidden");
            await AssertProblemAsync(await denied.DeleteAsync(
                $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments/{comment.Id}?rowVersion={Uri.EscapeDataString(updated.GetProperty("rowVersion").GetString()!)}"),
                HttpStatusCode.Forbidden, "forbidden");
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        TaskItemComment persisted = await db.TaskItemComments.SingleAsync(x => x.Id == comment.Id);
        persisted.Content.Should().Be("更新內容\n第二行");
        AuditLog audit = await db.AuditLogs.SingleAsync(x =>
            x.EntityType == "TaskItemComment" && x.EntityId == comment.Id.ToString() && x.Action == "Update");
        audit.ActorAccountId.Should().Be(authorId).And.NotBe(otherId).And.NotBe(viewerId);
        JsonDocument.Parse(audit.BeforeData!).RootElement.GetProperty("Content").GetString()
            .Should().Be("原內容");
        JsonDocument.Parse(audit.AfterData!).RootElement.GetProperty("Content").GetString()
            .Should().Be("更新內容\n第二行");
        audit.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    // 測試案例：TC-ERR-CMT-006、TC-SQL-005（stale／空白／非法 Base64 rowVersion）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 留言舊RowVersion更新與刪除皆應衝突且不覆蓋勝出內容()
    {
        (Guid ownerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid authorId, string authorAccount) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, [authorId]);
        TaskItem task = await CreateTaskAsync(project.Id, ownerId, authorId);
        TaskItemComment comment = await CreateCommentAsync(task.Id, authorId, "版本原值", DateTimeOffset.UtcNow);
        using HttpClient author = await CreateAuthenticatedClientAsync(authorAccount);
        JsonElement original = (await GetJsonAsync(author,
            $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments"))[0];
        string stale = original.GetProperty("rowVersion").GetString()!;
        JsonElement winner = await PutJsonAsync(author,
            $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments/{comment.Id}",
            new { content = "勝出內容", rowVersion = stale });

        await AssertProblemAsync(await author.PutAsJsonAsync(
            $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments/{comment.Id}",
            new { content = "失敗內容", rowVersion = stale }), HttpStatusCode.Conflict, "concurrency_conflict");
        await AssertProblemAsync(await author.DeleteAsync(
            $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments/{comment.Id}?rowVersion={Uri.EscapeDataString(stale)}"),
            HttpStatusCode.Conflict, "concurrency_conflict");
        foreach (string invalidRowVersion in new[] { "", "   ", "not-base64" })
        {
            await AssertProblemAsync(await author.PutAsJsonAsync(
                $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments/{comment.Id}",
                new { content = "非法版本不得更新", rowVersion = invalidRowVersion }),
                HttpStatusCode.Conflict, "concurrency_conflict");
        }

        JsonElement persisted = (await GetJsonAsync(author,
            $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments"))[0];
        persisted.GetProperty("content").GetString().Should().Be("勝出內容");
        persisted.GetProperty("rowVersion").GetString().Should().Be(winner.GetProperty("rowVersion").GetString());
    }

    // 測試案例：TC-ST-CMT-007、TC-ERR-CMT-008（軟刪除、關聯 scope 與 403／404 優先序）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 留言刪除應軟刪除且跨資源與不存在路徑遵守403與404契約()
    {
        (Guid ownerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid authorId, string authorAccount) = await CreateUserAsync(SystemRoles.User);
        (_, string outsiderAccount) = await CreateUserAsync(SystemRoles.User);
        Project projectA = await CreateProjectAsync(ownerId, [authorId]);
        Project projectB = await CreateProjectAsync(ownerId, []);
        TaskItem taskA = await CreateTaskAsync(projectA.Id, ownerId, authorId);
        TaskItem taskB = await CreateTaskAsync(projectA.Id, ownerId, authorId);
        TaskItemComment comment = await CreateCommentAsync(taskA.Id, authorId, "刪除目標", DateTimeOffset.UtcNow);
        using HttpClient author = await CreateAuthenticatedClientAsync(authorAccount);
        JsonElement current = (await GetJsonAsync(author,
            $"/api/v1/projects/{projectA.Id}/task-items/{taskA.Id}/comments"))[0];

        await AssertProblemAsync(await author.PutAsJsonAsync(
            $"/api/v1/projects/{projectA.Id}/task-items/{taskB.Id}/comments/{comment.Id}",
            new { content = "跨 Task", rowVersion = current.GetProperty("rowVersion").GetString() }),
            HttpStatusCode.NotFound, "not_found");
        await AssertProblemAsync(await author.DeleteAsync(
            $"/api/v1/projects/{projectA.Id}/task-items/{taskB.Id}/comments/{comment.Id}?rowVersion={Uri.EscapeDataString(current.GetProperty("rowVersion").GetString()!)}"),
            HttpStatusCode.NotFound, "not_found");
        await AssertProblemAsync(await author.GetAsync(
            $"/api/v1/projects/{projectA.Id}/task-items/{Guid.NewGuid()}/comments"),
            HttpStatusCode.NotFound, "not_found");

        using HttpClient outsider = await CreateAuthenticatedClientAsync(outsiderAccount);
        foreach (string path in new[]
                 {
                     $"/api/v1/projects/{projectA.Id}/task-items/{taskA.Id}/comments",
                     $"/api/v1/projects/{projectA.Id}/task-items/{Guid.NewGuid()}/comments",
                     $"/api/v1/projects/{projectB.Id}/task-items/{taskA.Id}/comments"
                 })
        {
            await AssertProblemAsync(await outsider.GetAsync(path), HttpStatusCode.Forbidden, "forbidden");
        }

        HttpResponseMessage deleted = await author.DeleteAsync(
            $"/api/v1/projects/{projectA.Id}/task-items/{taskA.Id}/comments/{comment.Id}?rowVersion={Uri.EscapeDataString(current.GetProperty("rowVersion").GetString()!)}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetJsonAsync(author, $"/api/v1/projects/{projectA.Id}/task-items/{taskA.Id}/comments"))
            .GetArrayLength().Should().Be(0);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.TaskItemComments.IgnoreQueryFilters().SingleAsync(x => x.Id == comment.Id)).DeletedAt.Should().NotBeNull();
        (await db.AuditLogs.CountAsync(x => x.EntityId == comment.Id.ToString() && x.Action == "Delete")).Should().Be(1);
    }

    // 測試案例：TC-ERR-CMT-009（DB 儲存失敗、交易完整性與明確重送）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 留言儲存失敗不得留下半套資料且明確重送只異動一次()
    {
        (Guid ownerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid authorId, string authorAccount) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, [authorId]);
        TaskItem task = await CreateTaskAsync(project.Id, ownerId, authorId);
        TaskItemComment existing = await CreateCommentAsync(task.Id, authorId, "編輯前內容", DateTimeOffset.UtcNow);
        using HttpClient author = await CreateAuthenticatedClientAsync(authorAccount);
        string path = $"/api/v1/projects/{project.Id}/task-items/{task.Id}/comments";

        _failureInterceptor.Arm();
        HttpResponseMessage failedCreate = await author.PostAsJsonAsync(path, new { content = "第一行\n第二行" });
        await AssertUnexpectedProblemAsync(failedCreate);
        await AssertCommentPersistenceAsync(task.Id, expectedCount: 1, expectedCreateAudits: 0, expectedUpdateAudits: 0);

        JsonElement created = await PostJsonAsync(author, path, new { content = "第一行\n第二行" });
        _commentIds.Add(created.GetProperty("id").GetGuid());
        await AssertCommentPersistenceAsync(task.Id, expectedCount: 2, expectedCreateAudits: 1, expectedUpdateAudits: 0);

        JsonElement current = (await GetJsonAsync(author, path))
            .EnumerateArray().Single(comment => comment.GetProperty("id").GetGuid() == existing.Id);
        var updateRequest = new
        {
            content = "修改第一行\n修改第二行",
            rowVersion = current.GetProperty("rowVersion").GetString()
        };
        _failureInterceptor.Arm();
        HttpResponseMessage failedUpdate = await author.PutAsJsonAsync($"{path}/{existing.Id}", updateRequest);
        await AssertUnexpectedProblemAsync(failedUpdate);
        await AssertCommentPersistenceAsync(task.Id, expectedCount: 2, expectedCreateAudits: 1, expectedUpdateAudits: 0);

        JsonElement updated = await PutJsonAsync(author, $"{path}/{existing.Id}", updateRequest);
        updated.GetProperty("content").GetString().Should().Be("修改第一行\n修改第二行");
        await AssertCommentPersistenceAsync(task.Id, expectedCount: 2, expectedCreateAudits: 1, expectedUpdateAudits: 1);
    }

    private async Task AssertCommentPersistenceAsync(
        Guid taskId,
        int expectedCount,
        int expectedCreateAudits,
        int expectedUpdateAudits)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Guid[] commentIds = await db.TaskItemComments.Where(comment => comment.TaskItemId == taskId)
            .Select(comment => comment.Id).ToArrayAsync();
        (await db.TaskItemComments.CountAsync(comment => comment.TaskItemId == taskId)).Should().Be(expectedCount);
        string[] entityIds = commentIds.Select(id => id.ToString()).ToArray();
        (await db.AuditLogs.CountAsync(log => log.EntityType == "TaskItemComment" && log.Action == "Create" &&
                                              entityIds.Contains(log.EntityId))).Should().Be(expectedCreateAudits);
        (await db.AuditLogs.CountAsync(log => log.EntityType == "TaskItemComment" && log.Action == "Update" &&
                                              entityIds.Contains(log.EntityId))).Should().Be(expectedUpdateAudits);
    }

    private async Task<(Guid Id, string Account)> CreateUserAsync(string role)
    {
        string account = $"comment-api-{role.ToLowerInvariant()}-{Guid.NewGuid():N}";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = account,
            Email = $"{account}@example.test",
            Name = $"{role} 留言測試",
            EmailConfirmed = true,
            IsEnabled = true
        };
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await manager.CreateAsync(user, ValidPassword)).Succeeded.Should().BeTrue();
        (await manager.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        db.UserPreferences.Add(new UserPreference(user.Id));
        await db.SaveChangesAsync();
        _accountIds.Add(user.Id);
        return (user.Id, account);
    }

    private async Task<Project> CreateProjectAsync(Guid ownerId, IReadOnlyCollection<Guid> members)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new Project(Guid.NewGuid(), $"PRJ-C{Guid.NewGuid():N}"[..20], $"Comment API {Guid.NewGuid():N}", null, ownerId, "Asia/Taipei", now);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Guid roleId = await db.ProjectRoles.Where(x => x.Code == ProjectRoleCodes.ProjectManager).Select(x => x.Id).SingleAsync();
        db.Projects.Add(project);
        foreach (Guid accountId in members.Append(ownerId).Distinct())
        {
            db.ProjectMembers.Add(new ProjectMember(project.Id, accountId, now));
            db.ProjectMemberRoles.Add(new ProjectMemberRole(project.Id, accountId, roleId));
        }
        await db.SaveChangesAsync();
        _projectIds.Add(project.Id);
        return project;
    }

    private async Task<TaskItem> CreateTaskAsync(Guid projectId, Guid creatorId, Guid assigneeId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var task = new TaskItem(Guid.NewGuid(), $"TASK-C{Guid.NewGuid():N}"[..22], projectId, creatorId, assigneeId,
            "留言測試 Task", null, now, now.AddDays(1), now);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.TaskItems.Add(task);
        await db.SaveChangesAsync();
        _taskIds.Add(task.Id);
        return task;
    }

    private async Task<TaskItemComment> CreateCommentAsync(Guid taskId, Guid authorId, string content,
        DateTimeOffset createdAt, bool deleted = false)
    {
        var comment = new TaskItemComment(Guid.NewGuid(), taskId, authorId, content, createdAt);
        if (deleted) comment.SoftDelete(createdAt.AddMilliseconds(1));
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.TaskItemComments.Add(comment);
        await db.SaveChangesAsync();
        _commentIds.Add(comment.Id);
        return comment;
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

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, object request)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(path, request);
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

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, body);
        JsonElement problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be(code);
        problem.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        body.Should().NotContain("System.").And.NotContain("Microsoft.Data.SqlClient").And.NotContain("accessToken");
    }

    private static async Task AssertUnexpectedProblemAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, body);
        JsonElement problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("status").GetInt32().Should().Be(500);
        problem.GetProperty("code").GetString().Should().Be("internal_server_error");
        problem.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        body.Should().NotContain("測試用留言儲存失敗");
        body.Should().NotContain("System.InvalidOperationException");
        body.Should().NotContain("Microsoft.Data.SqlClient");
        body.Should().NotContain("accessToken");
    }

    private async Task CleanupTestDataAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (_commentIds.Count > 0)
        {
            string[] ids = _commentIds.Select(x => x.ToString()).ToArray();
            await db.AuditLogs.Where(x => x.EntityType == "TaskItemComment" && ids.Contains(x.EntityId)).ExecuteDeleteAsync();
            await db.TaskItemComments.IgnoreQueryFilters().Where(x => _commentIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
        if (_taskIds.Count > 0)
        {
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
}
