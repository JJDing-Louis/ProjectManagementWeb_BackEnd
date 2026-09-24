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
using ProjectManagementWeb.Domain.Enums;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.IntegrationTests;

[NonParallelizable]
public sealed class ProjectApiTests
{
    private const string ValidPassword = "Test_password123!";
    private readonly HashSet<Guid> _accountIds = [];
    private readonly HashSet<Guid> _projectIds = [];
    private readonly HashSet<Guid> _taskIds = [];
    private TestWebApplicationFactory _factory = null!;
    private string _bootstrapAdminAccount = null!;

    [SetUp]
    public void SetUp()
    {
        string? connectionString = Environment.GetEnvironmentVariable("PMW_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Ignore("未設定 PMW_TEST_SQL_CONNECTION，略過實際 SQL Server Project API 測試。");
        }
        _bootstrapAdminAccount = $"bootstrap-project-{Guid.NewGuid():N}";
        _factory = new TestWebApplicationFactory(connectionString, _bootstrapAdminAccount);
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

    // 測試案例：TC-F-PRJ-001（一般成員、非成員、Administrator、Admin 與軟刪除範圍）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Project清單與詳情應只回傳角色可存取且未刪除的資料()
    {
        (Guid memberId, string memberAccount) = await CreateUserAsync(SystemRoles.User);
        (Guid ownerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        (_, string administratorAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (_, string adminAccount) = await CreateUserAsync(SystemRoles.Admin);
        Project projectA = await CreateProjectAsync(ownerId, "成員可見 A", members: [memberId]);
        Project projectB = await CreateProjectAsync(ownerId, "成員不可見 B");
        Project deleted = await CreateProjectAsync(ownerId, "已刪除 C", members: [memberId], deleted: true);

        using HttpClient member = await CreateAuthenticatedClientAsync(memberAccount);
        JsonElement memberList = await GetJsonAsync(member, "/api/v1/projects?pageSize=100");
        Guid[] memberProjectIds = memberList.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid()).ToArray();
        memberProjectIds.Should().Contain(projectA.Id).And.NotContain(projectB.Id).And.NotContain(deleted.Id);
        (await GetJsonAsync(member, $"/api/v1/projects/{projectA.Id}"))
            .GetProperty("id").GetGuid().Should().Be(projectA.Id);
        await AssertProblemAsync(
            await member.GetAsync($"/api/v1/projects/{projectB.Id}"), HttpStatusCode.Forbidden, "forbidden");

        foreach (string account in new[] { administratorAccount, adminAccount })
        {
            using HttpClient manager = await CreateAuthenticatedClientAsync(account);
            JsonElement list = await GetJsonAsync(manager, "/api/v1/projects?pageSize=100");
            Guid[] projectIds = list.GetProperty("items").EnumerateArray()
                .Select(x => x.GetProperty("id").GetGuid()).ToArray();
            projectIds.Should().Contain(projectA.Id).And.Contain(projectB.Id).And.NotContain(deleted.Id);
            (await GetJsonAsync(manager, $"/api/v1/projects/{projectB.Id}"))
                .GetProperty("id").GetGuid().Should().Be(projectB.Id);
        }
    }

    // 測試案例：TC-F-PRJ-002（code/name/description 搜尋、status、CreatedAt 排序、分頁與空結果）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Project查詢應正確套用單一搜尋狀態排序與分頁()
    {
        (Guid ownerId, string administratorAccount) = await CreateUserAsync(SystemRoles.Administrator);
        string marker = Guid.NewGuid().ToString("N")[..10];
        Project oldest = await CreateProjectAsync(
            ownerId, $"名稱-{marker}-舊", description: "一般說明", createdAt: DateTimeOffset.UtcNow.AddMinutes(-3));
        Project middle = await CreateProjectAsync(
            ownerId, "一般名稱", description: $"說明-{marker}-中", status: ProjectStatus.Active,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        Project newest = await CreateProjectAsync(
            ownerId, $"名稱-{marker}-新", description: "一般說明", status: ProjectStatus.Active,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        using HttpClient admin = await CreateAuthenticatedClientAsync(administratorAccount);

        JsonElement byName = await GetJsonAsync(admin, $"/api/v1/projects?search={marker}&pageSize=1&page=1");
        byName.GetProperty("totalCount").GetInt32().Should().Be(3);
        byName.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(newest.Id);
        JsonElement secondPage = await GetJsonAsync(admin, $"/api/v1/projects?search={marker}&pageSize=1&page=2");
        secondPage.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(middle.Id);

        JsonElement byDescription = await GetJsonAsync(
            admin, $"/api/v1/projects?search={Uri.EscapeDataString($"說明-{marker}-中")}");
        byDescription.GetProperty("items").GetArrayLength().Should().Be(1);
        byDescription.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(middle.Id);

        JsonElement byCode = await GetJsonAsync(admin, $"/api/v1/projects?search={oldest.Code}");
        byCode.GetProperty("items").GetArrayLength().Should().Be(1);
        byCode.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(oldest.Id);

        JsonElement active = await GetJsonAsync(
            admin, $"/api/v1/projects?search={marker}&status={ProjectStatus.Active}&page=0&pageSize=500");
        active.GetProperty("page").GetInt32().Should().Be(1);
        active.GetProperty("pageSize").GetInt32().Should().Be(100);
        active.GetProperty("totalCount").GetInt32().Should().Be(2);
        active.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid())
            .Should().Equal(newest.Id, middle.Id);

        JsonElement empty = await GetJsonAsync(admin, "/api/v1/projects?search=no-such-pmw-project");
        empty.GetProperty("totalCount").GetInt32().Should().Be(0);
        empty.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // 測試案例：TC-F-PRJ-003、TC-ERR-PRJ-004、TC-SQL-006（建立時 Owner 完整資格、JWT actor 與資料關聯）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:47:48 +08:00
    [Test]
    public async Task 建立Project只允許已啟用已驗證的Administrator擔任Owner()
    {
        (Guid actorId, string actorAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid validOwnerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid adminId, _) = await CreateUserAsync(SystemRoles.Admin);
        (Guid userId, _) = await CreateUserAsync(SystemRoles.User);
        (Guid viewerId, string viewerAccount) = await CreateUserAsync(SystemRoles.Viewer);
        (Guid disabledId, _) = await CreateUserAsync(SystemRoles.Administrator, enabled: false);
        (Guid unverifiedId, _) = await CreateUserAsync(SystemRoles.Administrator, emailConfirmed: false);
        using HttpClient actor = await CreateAuthenticatedClientAsync(actorAccount);
        using HttpClient viewer = await CreateAuthenticatedClientAsync(viewerAccount);
        string marker = Guid.NewGuid().ToString("N")[..10];

        await AssertProblemAsync(
            await viewer.PostAsJsonAsync("/api/v1/projects", new
            {
                name = $"Owner-forbidden-{marker}",
                description = "不得建立",
                ownerAccountId = validOwnerId,
                timeZoneId = "Asia/Taipei"
            }),
            HttpStatusCode.Forbidden,
            "forbidden");

        foreach (Guid invalidOwnerId in new[] { Guid.NewGuid(), adminId, userId, viewerId, disabledId, unverifiedId })
        {
            HttpResponseMessage invalidResponse = await actor.PostAsJsonAsync("/api/v1/projects", new
            {
                name = $"Owner-invalid-{marker}",
                description = "不得建立",
                ownerAccountId = invalidOwnerId,
                timeZoneId = "Asia/Taipei"
            });
            if (invalidResponse.StatusCode == HttpStatusCode.Created)
            {
                JsonElement unexpectedlyCreated = JsonDocument.Parse(
                    await invalidResponse.Content.ReadAsStringAsync()).RootElement;
                _projectIds.Add(unexpectedlyCreated.GetProperty("id").GetGuid());
            }
            await AssertProblemAsync(
                invalidResponse,
                HttpStatusCode.UnprocessableEntity,
                "invalid_owner");
        }

        HttpResponseMessage response = await actor.PostAsJsonAsync("/api/v1/projects", new
        {
            name = $"  Owner-valid-{marker}  ",
            description = "  合法建立  ",
            ownerAccountId = validOwnerId,
            timeZoneId = "Asia/Taipei"
        });
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        JsonElement created = JsonDocument.Parse(body).RootElement;
        Guid projectId = created.GetProperty("id").GetGuid();
        _projectIds.Add(projectId);
        created.GetProperty("name").GetString().Should().Be($"Owner-valid-{marker}");
        created.GetProperty("description").GetString().Should().Be("合法建立");
        created.GetProperty("ownerAccountId").GetGuid().Should().Be(validOwnerId);
        created.GetProperty("status").GetString().Should().Be(ProjectStatus.Pending.ToString());
        created.GetProperty("versionNumber").GetInt32().Should().Be(1);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Guid managerRoleId = await db.ProjectRoles.Where(x => x.Code == ProjectRoleCodes.ProjectManager)
            .Select(x => x.Id).SingleAsync();
        (await db.Projects.CountAsync(x => x.Name == $"Owner-invalid-{marker}")).Should().Be(0);
        (await db.ProjectMembers.AnyAsync(x => x.ProjectId == projectId && x.AccountId == validOwnerId)).Should().BeTrue();
        (await db.ProjectMemberRoles.AnyAsync(x => x.ProjectId == projectId && x.AccountId == validOwnerId &&
                                                x.ProjectRoleId == managerRoleId)).Should().BeTrue();
        AuditLog createAudit = await db.AuditLogs.SingleAsync(x =>
            x.Action == "Create" && x.EntityType == "Project" && x.EntityId == projectId.ToString());
        createAudit.ActorAccountId.Should().Be(actorId).And.NotBe(validOwnerId);
        createAudit.BeforeData.Should().BeNull();
        createAudit.AfterData.Should().Contain($"Owner-valid-{marker}");
        createAudit.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        (await db.AuditLogs.CountAsync(x => x.EntityType == "Project" &&
                                             x.EntityId != projectId.ToString() &&
                                             x.AfterData != null &&
                                             x.AfterData.Contains($"Owner-invalid-{marker}")))
            .Should().Be(0);
    }

    // 測試案例：TC-ST-PRJ-006、TC-ERR-PRJ-004（修改 Owner 完整資格）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 10:01:56 +08:00
    [Test]
    public async Task 修改ProjectOwner時應同時驗證成員關係與帳號完整資格()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid validOwnerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid adminId, _) = await CreateUserAsync(SystemRoles.Admin);
        (Guid userId, _) = await CreateUserAsync(SystemRoles.User);
        (Guid viewerId, _) = await CreateUserAsync(SystemRoles.Viewer);
        (Guid disabledId, _) = await CreateUserAsync(SystemRoles.Administrator, enabled: false);
        (Guid unverifiedId, _) = await CreateUserAsync(SystemRoles.Administrator, emailConfirmed: false);
        Project project = await CreateProjectAsync(
            ownerId,
            "Owner 修改資格",
            members: [validOwnerId, adminId, userId, viewerId, disabledId, unverifiedId]);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);

        foreach (Guid invalidOwnerId in new[] { Guid.NewGuid(), adminId, userId, viewerId, disabledId, unverifiedId })
        {
            JsonElement current = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}");
            await AssertProblemAsync(
                await owner.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new
                {
                    name = "不得更新 Owner",
                    description = "不得保存",
                    ownerAccountId = invalidOwnerId,
                    timeZoneId = "Asia/Taipei",
                    status = ProjectStatus.Active,
                    rowVersion = current.GetProperty("rowVersion").GetString()
                }),
                HttpStatusCode.UnprocessableEntity,
                "invalid_owner");
        }

        JsonElement before = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}");
        JsonElement updated = await PutJsonAsync(owner, $"/api/v1/projects/{project.Id}", new
        {
            name = "合法移交 Owner",
            description = "已保存",
            ownerAccountId = validOwnerId,
            timeZoneId = "Asia/Taipei",
            status = ProjectStatus.Completed,
            rowVersion = before.GetProperty("rowVersion").GetString()
        });
        updated.GetProperty("ownerAccountId").GetGuid().Should().Be(validOwnerId);
        updated.GetProperty("status").GetString().Should().Be(ProjectStatus.Completed.ToString());
    }

    // 測試案例：TC-E-PRJ-005（Project 名稱與說明 API／SQL 邊界）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 16:30:21 +08:00
    [Test]
    public async Task Project建立與修改應套用名稱及說明長度邊界並回欄位錯誤()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);

        foreach ((string name, string? description, string field) in new[]
                 {
                     ("   ", (string?)null, "name"),
                     (new string('名', 201), (string?)null, "name"),
                     ("合法名稱", (string?)new string('說', 4001), "description")
                 })
        {
            HttpResponseMessage invalid = await owner.PostAsJsonAsync("/api/v1/projects", new
            {
                name,
                description,
                ownerAccountId = ownerId,
                timeZoneId = "Asia/Taipei"
            });
            await AssertValidationFieldAsync(invalid, field);
        }

        foreach ((string name, string? description) in new[]
                 {
                     ("名", (string?)null),
                     (new string('名', 200), (string?)new string('說', 4000))
                 })
        {
            HttpResponseMessage response = await owner.PostAsJsonAsync("/api/v1/projects", new
            {
                name,
                description,
                ownerAccountId = ownerId,
                timeZoneId = "Asia/Taipei"
            });
            string body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Created, body);
            JsonElement created = JsonDocument.Parse(body).RootElement;
            _projectIds.Add(created.GetProperty("id").GetGuid());
            created.GetProperty("name").GetString().Should().HaveLength(name.Length);
            if (description is not null)
            {
                created.GetProperty("description").GetString().Should().HaveLength(description.Length);
            }
        }

        Project project = await CreateProjectAsync(ownerId, "修改邊界原始資料");
        foreach ((string name, string? description, string field) in new[]
                 {
                     ("   ", (string?)null, "name"),
                     (new string('名', 201), (string?)null, "name"),
                     ("合法名稱", (string?)new string('說', 4001), "description")
                 })
        {
            JsonElement current = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}");
            HttpResponseMessage invalid = await owner.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new
            {
                name,
                description,
                ownerAccountId = ownerId,
                timeZoneId = "Asia/Taipei",
                status = ProjectStatus.Active,
                rowVersion = current.GetProperty("rowVersion").GetString()
            });
            await AssertValidationFieldAsync(invalid, field);
        }

        JsonElement persisted = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}");
        persisted.GetProperty("name").GetString().Should().Be("修改邊界原始資料");
        persisted.GetProperty("status").GetString().Should().Be(ProjectStatus.Pending.ToString());
    }

    // 測試案例：TC-F-PRJ-003、TC-ST-PRJ-006（Project IANA TimeZoneId 建立、驗證與修改）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:20:57 +08:00
    [Test]
    public async Task Project建立與修改應要求明確合法IanaTimeZoneId並持久化()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);

        foreach (string? invalidTimeZoneId in new[] { null, "   ", "Taipei Standard Time", "Invalid/Zone" })
        {
            HttpResponseMessage invalid = await owner.PostAsJsonAsync("/api/v1/projects", new
            {
                name = "無效時區不得建立",
                ownerAccountId = ownerId,
                timeZoneId = invalidTimeZoneId
            });
            await AssertValidationFieldAsync(invalid, "timeZoneId");
        }

        HttpResponseMessage response = await owner.PostAsJsonAsync("/api/v1/projects", new
        {
            name = "時區測試專案",
            ownerAccountId = ownerId,
            timeZoneId = "Asia/Taipei"
        });
        string responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, responseBody);
        JsonElement created = JsonDocument.Parse(responseBody).RootElement;
        Guid projectId = created.GetProperty("id").GetGuid();
        _projectIds.Add(projectId);
        created.GetProperty("timeZoneId").GetString().Should().Be("Asia/Taipei");

        HttpResponseMessage updatedResponse = await owner.PutAsJsonAsync($"/api/v1/projects/{projectId}", new
        {
            name = "時區測試專案",
            description = (string?)null,
            ownerAccountId = ownerId,
            timeZoneId = "America/New_York",
            status = ProjectStatus.Active,
            rowVersion = created.GetProperty("rowVersion").GetString()
        });
        string updatedBody = await updatedResponse.Content.ReadAsStringAsync();
        updatedResponse.StatusCode.Should().Be(HttpStatusCode.OK, updatedBody);
        JsonDocument.Parse(updatedBody).RootElement.GetProperty("timeZoneId").GetString()
            .Should().Be("America/New_York");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.Projects.Where(x => x.Id == projectId).Select(x => x.TimeZoneId).SingleAsync())
            .Should().Be("America/New_York");
    }

    // 測試案例：TC-ST-PRJ-008（四種 Project status 的 16 組 API 互轉）
    // 測試結果：Passed（16/16 狀態組合）
    // 上次測試時間：2026-09-16 10:01:56 +08:00
    [Test]
    public async Task Project四種Status應在Api層允許全部十六組互轉並增加版本與稽核()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);
        var transitionedProjectIds = new List<Guid>();

        foreach (ProjectStatus source in Enum.GetValues<ProjectStatus>())
        {
            foreach (ProjectStatus target in Enum.GetValues<ProjectStatus>())
            {
                Project project = await CreateProjectAsync(
                    ownerId,
                    $"狀態互轉 {source} to {target}",
                    status: source);
                transitionedProjectIds.Add(project.Id);
                JsonElement before = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}");

                JsonElement updated = await PutJsonAsync(owner, $"/api/v1/projects/{project.Id}", new
                {
                    name = $"已轉為 {target}",
                    description = $"{source} to {target}",
                    ownerAccountId = ownerId,
                    timeZoneId = "Asia/Taipei",
                    status = target,
                    rowVersion = before.GetProperty("rowVersion").GetString()
                });

                updated.GetProperty("status").GetString().Should().Be(target.ToString());
                updated.GetProperty("versionNumber").GetInt32()
                    .Should().Be(before.GetProperty("versionNumber").GetInt32() + 1);
                updated.GetProperty("rowVersion").GetString()
                    .Should().NotBe(before.GetProperty("rowVersion").GetString());
            }
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        AuditLog[] audits = await db.AuditLogs.Where(x =>
                x.Action == "Update" && transitionedProjectIds.Select(id => id.ToString()).Contains(x.EntityId))
            .ToArrayAsync();
        audits.Should().HaveCount(16);
        audits.Should().OnlyContain(x => x.BeforeData != null && x.AfterData != null);
    }

    // 測試案例：TC-ERR-PRJ-007、TC-SQL-005（stale／空白／非法 Base64 rowVersion）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Project使用舊RowVersion更新應回衝突且不得覆蓋已提交資料()
    {
        (Guid ownerId, string administratorAccount) = await CreateUserAsync(SystemRoles.Administrator);
        Project project = await CreateProjectAsync(ownerId, "衝突前名稱", description: "原說明");
        using HttpClient clientA = await CreateAuthenticatedClientAsync(administratorAccount);
        using HttpClient clientB = await CreateAuthenticatedClientAsync(administratorAccount);
        JsonElement original = await GetJsonAsync(clientA, $"/api/v1/projects/{project.Id}");
        string originalRowVersion = original.GetProperty("rowVersion").GetString()!;

        JsonElement updated = await PutJsonAsync(clientA, $"/api/v1/projects/{project.Id}", new
        {
            name = "Client A 已更新",
            description = "A 說明",
            ownerAccountId = ownerId,
            timeZoneId = "Asia/Taipei",
            status = ProjectStatus.Active,
            rowVersion = originalRowVersion
        });
        updated.GetProperty("versionNumber").GetInt32().Should().Be(original.GetProperty("versionNumber").GetInt32() + 1);
        updated.GetProperty("rowVersion").GetString().Should().NotBe(originalRowVersion);

        await AssertProblemAsync(
            await clientB.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new
            {
                name = "Client B 不應覆蓋",
                description = "B 說明",
                ownerAccountId = ownerId,
                timeZoneId = "Asia/Taipei",
                status = ProjectStatus.Completed,
                rowVersion = originalRowVersion
            }),
            HttpStatusCode.Conflict,
            "concurrency_conflict");
        foreach (string invalidRowVersion in new[] { "", "   ", "not-base64" })
        {
            await AssertProblemAsync(await clientB.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new
            {
                name = "非法版本不得更新",
                description = "不得保存",
                ownerAccountId = ownerId,
                timeZoneId = "Asia/Taipei",
                status = ProjectStatus.Completed,
                rowVersion = invalidRowVersion
            }), HttpStatusCode.Conflict, "concurrency_conflict");
        }

        JsonElement persisted = await GetJsonAsync(clientA, $"/api/v1/projects/{project.Id}");
        persisted.GetProperty("name").GetString().Should().Be("Client A 已更新");
        persisted.GetProperty("description").GetString().Should().Be("A 說明");
        persisted.GetProperty("status").GetString().Should().Be(ProjectStatus.Active.ToString());
        persisted.GetProperty("rowVersion").GetString().Should().Be(updated.GetProperty("rowVersion").GetString());
    }

    // 測試案例：TC-ST-PRJ-009（Administrator、Admin 軟刪除、子資料保留與稽核）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:20:57 +08:00
    [Test]
    public async Task Administrator與Admin軟刪除Project應隱藏專案並保留子資料及刪除稽核()
    {
        (Guid ownerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid memberId, _) = await CreateUserAsync(SystemRoles.User);
        (Guid administratorId, string administratorAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid adminId, string adminAccount) = await CreateUserAsync(SystemRoles.Admin);

        foreach ((Guid actorId, string actorAccount) in new[]
                 {
                     (administratorId, administratorAccount),
                     (adminId, adminAccount)
                 })
        {
            Project project = await CreateProjectAsync(ownerId, $"軟刪除-{actorAccount}", members: [memberId]);
            TaskItem task = await CreateTaskAsync(
                project.Id,
                ownerId,
                memberId,
                ProjectManagementWeb.Domain.Enums.TaskStatus.Pending);
            Guid commentId = Guid.NewGuid();
            Guid historyId = Guid.NewGuid();
            await using (AsyncServiceScope setupScope = _factory.Services.CreateAsyncScope())
            {
                ApplicationDbContext setupDb = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                DateTimeOffset now = DateTimeOffset.UtcNow;
                setupDb.TaskItemComments.Add(new TaskItemComment(commentId, task.Id, memberId, "軟刪除後仍應保留", now));
                setupDb.TaskItemHistories.Add(new TaskItemHistory(historyId, task.Id, ownerId, "Create", "{}", now));
                await setupDb.SaveChangesAsync();
            }

            using HttpClient actor = await CreateAuthenticatedClientAsync(actorAccount);
            JsonElement current = await GetJsonAsync(actor, $"/api/v1/projects/{project.Id}");
            HttpResponseMessage response = await actor.DeleteAsync(
                $"/api/v1/projects/{project.Id}?rowVersion={Uri.EscapeDataString(current.GetProperty("rowVersion").GetString()!)}");
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

            JsonElement list = await GetJsonAsync(actor, "/api/v1/projects?pageSize=100");
            list.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid())
                .Should().NotContain(project.Id);
            await AssertProblemAsync(
                await actor.GetAsync($"/api/v1/projects/{project.Id}"),
                HttpStatusCode.NotFound,
                "not_found");

            await using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
            ApplicationDbContext db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Project deleted = await db.Projects.IgnoreQueryFilters().SingleAsync(x => x.Id == project.Id);
            deleted.DeletedAt.Should().NotBeNull();
            deleted.DeletedByAccountId.Should().Be(actorId);
            (await db.ProjectMembers.CountAsync(x => x.ProjectId == project.Id)).Should().Be(2);
            (await db.TaskItems.IgnoreQueryFilters().CountAsync(x => x.Id == task.Id)).Should().Be(1);
            (await db.TaskItemComments.IgnoreQueryFilters().CountAsync(x => x.Id == commentId)).Should().Be(1);
            (await db.TaskItemHistories.CountAsync(x => x.Id == historyId)).Should().Be(1);
            AuditLog audit = await db.AuditLogs.SingleAsync(x =>
                x.EntityType == "Project" && x.EntityId == project.Id.ToString() && x.Action == "Delete");
            audit.ActorAccountId.Should().Be(actorId);
            audit.AfterData.Should().Contain(actorId.ToString());
        }
    }

    // 測試案例：TC-ERR-PRJ-010（授權優先、重複刪除、不存在、stale rowVersion 與無額外刪除入口）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:20:57 +08:00
    [Test]
    public async Task Project軟刪除失敗矩陣應維持資料不變且不提供復原或永久刪除()
    {
        (Guid ownerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        (_, string administratorAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (_, string adminAccount) = await CreateUserAsync(SystemRoles.Admin);
        (_, string userAccount) = await CreateUserAsync(SystemRoles.User);
        (_, string viewerAccount) = await CreateUserAsync(SystemRoles.Viewer);
        Project existing = await CreateProjectAsync(ownerId, "軟刪除失敗矩陣");
        Project alreadyDeleted = await CreateProjectAsync(ownerId, "已軟刪除", deleted: true);
        Guid missingId = Guid.NewGuid();
        string existingVersion;
        await using (AsyncServiceScope versionScope = _factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = versionScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            existingVersion = Convert.ToBase64String(
                await db.Projects.Where(x => x.Id == existing.Id).Select(x => x.RowVersion).SingleAsync());
        }

        foreach (string account in new[] { userAccount, viewerAccount })
        {
            using HttpClient unauthorized = await CreateAuthenticatedClientAsync(account);
            foreach (Guid projectId in new[] { existing.Id, alreadyDeleted.Id, missingId })
            {
                await AssertProblemAsync(
                    await unauthorized.DeleteAsync(
                        $"/api/v1/projects/{projectId}?rowVersion={Uri.EscapeDataString(existingVersion)}"),
                    HttpStatusCode.Forbidden,
                    "forbidden");
            }
        }

        foreach (string account in new[] { administratorAccount, adminAccount })
        {
            using HttpClient authorized = await CreateAuthenticatedClientAsync(account);
            foreach (Guid projectId in new[] { alreadyDeleted.Id, missingId })
            {
                await AssertProblemAsync(
                    await authorized.DeleteAsync(
                        $"/api/v1/projects/{projectId}?rowVersion={Uri.EscapeDataString(existingVersion)}"),
                    HttpStatusCode.NotFound,
                    "not_found");
            }
        }

        using HttpClient admin = await CreateAuthenticatedClientAsync(adminAccount);
        foreach (string invalidVersion in new[] { Convert.ToBase64String(new byte[] { 1, 2, 3 }), "not-base64" })
        {
            await AssertProblemAsync(
                await admin.DeleteAsync(
                    $"/api/v1/projects/{existing.Id}?rowVersion={Uri.EscapeDataString(invalidVersion)}"),
                HttpStatusCode.Conflict,
                "concurrency_conflict");
        }
        (await admin.PostAsync($"/api/v1/projects/{existing.Id}/restore", null)).StatusCode
            .Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        (await admin.DeleteAsync(
            $"/api/v1/projects/{existing.Id}/permanent?rowVersion={Uri.EscapeDataString(existingVersion)}")).StatusCode
            .Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);

        await using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Project unchanged = await verifyDb.Projects.SingleAsync(x => x.Id == existing.Id);
        unchanged.DeletedAt.Should().BeNull();
        unchanged.DeletedByAccountId.Should().BeNull();
        (await verifyDb.AuditLogs.CountAsync(x =>
            x.EntityType == "Project" && x.EntityId == existing.Id.ToString() && x.Action == "Delete")).Should().Be(0);
    }

    // 測試案例：TC-SEC-API-005（Project 與 Member 全 endpoint 的 403／404 優先序）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:39:03 +08:00
    [Test]
    public async Task Project與Member範圍端點應先授權再判斷資源是否存在()
    {
        (Guid ownerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid memberId, _) = await CreateUserAsync(SystemRoles.User);
        (_, string outsiderAccount) = await CreateUserAsync(SystemRoles.User);
        (_, string adminAccount) = await CreateUserAsync(SystemRoles.Admin);
        Project project = await CreateProjectAsync(ownerId, "Project scope 矩陣", members: [memberId]);
        Guid missingProjectId = Guid.NewGuid();
        Guid memberRoleId = await GetProjectRoleIdAsync(ProjectRoleCodes.Member);
        string rowVersion;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            rowVersion = Convert.ToBase64String(
                await db.Projects.Where(x => x.Id == project.Id).Select(x => x.RowVersion).SingleAsync());
        }

        Func<HttpClient, Guid, Task<HttpResponseMessage>>[] operations =
        [
            (client, id) => client.GetAsync($"/api/v1/projects/{id}"),
            (client, id) => client.PutAsJsonAsync($"/api/v1/projects/{id}", new
            {
                name = "Scope 更新",
                description = (string?)null,
                ownerAccountId = ownerId,
                timeZoneId = "Asia/Taipei",
                status = ProjectStatus.Active,
                rowVersion
            }),
            (client, id) => client.DeleteAsync(
                $"/api/v1/projects/{id}?rowVersion={Uri.EscapeDataString(rowVersion)}"),
            (client, id) => client.GetAsync($"/api/v1/projects/{id}/members"),
            (client, id) => client.GetAsync($"/api/v1/projects/{id}/member-candidates"),
            (client, id) => client.PostAsJsonAsync($"/api/v1/projects/{id}/members",
                new { accountId = memberId, projectRoleIds = new[] { memberRoleId } }),
            (client, id) => client.PutAsJsonAsync($"/api/v1/projects/{id}/members/{memberId}",
                new { projectRoleIds = new[] { memberRoleId } }),
            (client, id) => client.DeleteAsync($"/api/v1/projects/{id}/members/{memberId}")
        ];

        using HttpClient outsider = await CreateAuthenticatedClientAsync(outsiderAccount);
        foreach (Func<HttpClient, Guid, Task<HttpResponseMessage>> operation in operations)
        {
            await AssertProblemAsync(await operation(outsider, project.Id), HttpStatusCode.Forbidden, "forbidden");
            await AssertProblemAsync(await operation(outsider, missingProjectId), HttpStatusCode.Forbidden, "forbidden");
        }

        using HttpClient admin = await CreateAuthenticatedClientAsync(adminAccount);
        foreach (Func<HttpClient, Guid, Task<HttpResponseMessage>> operation in operations)
        {
            await AssertProblemAsync(await operation(admin, missingProjectId), HttpStatusCode.NotFound, "not_found");
        }

        (await GetJsonAsync(admin, $"/api/v1/projects/{project.Id}"))
            .GetProperty("id").GetGuid().Should().Be(project.Id);
        (await GetJsonAsync(admin, $"/api/v1/projects/{project.Id}/members"))
            .GetArrayLength().Should().Be(2);
        (await GetJsonAsync(admin, $"/api/v1/projects/{project.Id}/member-candidates?pageSize=1"))
            .GetProperty("pageSize").GetInt32().Should().Be(1);
    }

    // 測試案例：TC-F-MEMBER-001、TC-ERR-MEMBER-009、TC-ERR-USER-006（候選人最小揭露與排除規則）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 成員候選人應只揭露有效非成員且排除BootstrapAdmin()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        string marker = Guid.NewGuid().ToString("N")[..10];
        (Guid eligibleId, string eligibleAccount) = await CreateUserAsync(
            SystemRoles.User, account: $"{marker}-eligible");
        (Guid existingId, _) = await CreateUserAsync(SystemRoles.User, account: $"{marker}-existing");
        await CreateUserAsync(SystemRoles.User, enabled: false, account: $"{marker}-disabled");
        await CreateUserAsync(SystemRoles.Admin, account: _bootstrapAdminAccount);
        (_, string outsiderAccount) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, "候選人測試", members: [existingId]);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);

        JsonElement result = await GetJsonAsync(
            owner, $"/api/v1/projects/{project.Id}/member-candidates?search={marker}&page=0&pageSize=500");
        result.GetProperty("page").GetInt32().Should().Be(1);
        result.GetProperty("pageSize").GetInt32().Should().Be(100);
        result.GetProperty("totalCount").GetInt32().Should().Be(1);
        JsonElement candidate = result.GetProperty("items")[0];
        candidate.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo("id", "account", "name");
        candidate.GetProperty("id").GetGuid().Should().Be(eligibleId);
        candidate.GetProperty("account").GetString().Should().Be(eligibleAccount);

        await using (AsyncServiceScope disableScope = _factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext disableDb = disableScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ApplicationUser eligibleUser = await disableDb.Users.SingleAsync(x => x.Id == eligibleId);
            eligibleUser.IsEnabled = false;
            await disableDb.SaveChangesAsync();
        }
        Guid memberRoleId = await GetProjectRoleIdAsync(ProjectRoleCodes.Member);
        await AssertProblemAsync(
            await owner.PostAsJsonAsync($"/api/v1/projects/{project.Id}/members",
                new { accountId = eligibleId, projectRoleIds = new[] { memberRoleId } }),
            HttpStatusCode.UnprocessableEntity,
            "invalid_account");

        using HttpClient outsider = await CreateAuthenticatedClientAsync(outsiderAccount);
        await AssertProblemAsync(
            await outsider.GetAsync($"/api/v1/projects/{project.Id}/member-candidates"),
            HttpStatusCode.Forbidden,
            "forbidden");
    }

    // 測試案例：TC-F-MEMBER-002、TC-SQL-006（加入多角色成員與 JWT actor／audit）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:47:48 +08:00
    [Test]
    public async Task 加入多角色成員應建立單一Membership與完整角色集合()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid targetId, _) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, "加入成員測試");
        Guid frontendRoleId = await GetProjectRoleIdAsync(ProjectRoleCodes.FrontendDeveloper);
        Guid backendRoleId = await GetProjectRoleIdAsync(ProjectRoleCodes.BackendDeveloper);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);

        HttpResponseMessage response = await owner.PostAsJsonAsync(
            $"/api/v1/projects/{project.Id}/members",
            new { accountId = targetId, projectRoleIds = new[] { frontendRoleId, backendRoleId } });
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonElement member = JsonDocument.Parse(body).RootElement;
        member.GetProperty("roles").EnumerateArray().Select(x => x.GetProperty("id").GetGuid())
            .Should().BeEquivalentTo(new[] { frontendRoleId, backendRoleId });

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ProjectMembers.CountAsync(x => x.ProjectId == project.Id && x.AccountId == targetId)).Should().Be(1);
        (await db.ProjectMemberRoles.Where(x => x.ProjectId == project.Id && x.AccountId == targetId)
            .Select(x => x.ProjectRoleId).ToArrayAsync()).Should().BeEquivalentTo(new[] { frontendRoleId, backendRoleId });
        AuditLog audit = await db.AuditLogs.SingleAsync(x =>
            x.Action == "AddMember" && x.EntityType == "Project" && x.EntityId == project.Id.ToString());
        audit.ActorAccountId.Should().Be(ownerId).And.NotBe(targetId);
        audit.BeforeData.Should().BeNull();
        audit.AfterData.Should().Contain(targetId.ToString()).And.Contain(frontendRoleId.ToString())
            .And.Contain(backendRoleId.ToString());
        audit.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    // 測試案例：TC-ERR-MEMBER-003、TC-ERR-MEMBER-004、TC-ERR-MEMBER-009、TC-ERR-USER-006（新增失敗矩陣）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 新增成員的重複角色無效角色與無效帳號應符合固定契約()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid existingId, _) = await CreateUserAsync(SystemRoles.User);
        (Guid duplicateRoleTargetId, _) = await CreateUserAsync(SystemRoles.User);
        (Guid invalidRoleTargetId, _) = await CreateUserAsync(SystemRoles.User);
        (Guid disabledId, _) = await CreateUserAsync(SystemRoles.User, enabled: false);
        (Guid bootstrapId, _) = await CreateUserAsync(SystemRoles.Admin, account: _bootstrapAdminAccount);
        Project project = await CreateProjectAsync(ownerId, "成員錯誤矩陣", members: [existingId]);
        Guid memberRoleId = await GetProjectRoleIdAsync(ProjectRoleCodes.Member);
        Guid managerRoleId = await GetProjectRoleIdAsync(ProjectRoleCodes.ProjectManager);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);

        await AssertProblemAsync(
            await owner.PostAsJsonAsync($"/api/v1/projects/{project.Id}/members",
                new { accountId = existingId, projectRoleIds = new[] { memberRoleId } }),
            HttpStatusCode.Conflict,
            "duplicate_member");

        HttpResponseMessage duplicateRoleResponse = await owner.PostAsJsonAsync(
            $"/api/v1/projects/{project.Id}/members",
            new { accountId = duplicateRoleTargetId, projectRoleIds = new[] { memberRoleId, memberRoleId } });
        duplicateRoleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.ProjectMemberRoles.CountAsync(x =>
                x.ProjectId == project.Id && x.AccountId == duplicateRoleTargetId)).Should().Be(1);

            db.ProjectMembers.Add(new ProjectMember(project.Id, existingId, DateTimeOffset.UtcNow));
            Func<Task> saveDuplicateMembership = () => db.SaveChangesAsync();
            await saveDuplicateMembership.Should().ThrowAsync<DbUpdateException>();
            db.ChangeTracker.Clear();

            db.ProjectMemberRoles.Add(new ProjectMemberRole(project.Id, duplicateRoleTargetId, memberRoleId));
            Func<Task> saveDuplicateRole = () => db.SaveChangesAsync();
            await saveDuplicateRole.Should().ThrowAsync<DbUpdateException>();
        }

        foreach (Guid[] roles in new[] { Array.Empty<Guid>(), new[] { Guid.NewGuid() }, new[] { memberRoleId, Guid.NewGuid() } })
        {
            await AssertProblemAsync(
                await owner.PostAsJsonAsync($"/api/v1/projects/{project.Id}/members",
                    new { accountId = invalidRoleTargetId, projectRoleIds = roles }),
                HttpStatusCode.UnprocessableEntity,
                "invalid_project_roles");
            await AssertProblemAsync(
                await owner.PutAsJsonAsync($"/api/v1/projects/{project.Id}/members/{existingId}",
                    new { projectRoleIds = roles }),
                HttpStatusCode.UnprocessableEntity,
                "invalid_project_roles");
        }
        foreach ((Guid accountId, string code) in new[]
                 {
                     (Guid.NewGuid(), "invalid_account"),
                     (disabledId, "invalid_account"),
                     (bootstrapId, "bootstrap_admin_not_project_member")
                 })
        {
            await AssertProblemAsync(
                await owner.PostAsJsonAsync($"/api/v1/projects/{project.Id}/members",
                    new { accountId, projectRoleIds = new[] { memberRoleId } }),
                HttpStatusCode.UnprocessableEntity,
                code);
        }

        await using AsyncServiceScope finalScope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext finalDb = finalScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await finalDb.ProjectMembers.CountAsync(x =>
            x.ProjectId == project.Id && (x.AccountId == invalidRoleTargetId || x.AccountId == disabledId ||
                                          x.AccountId == bootstrapId))).Should().Be(0);
        (await finalDb.ProjectMemberRoles.Where(x => x.ProjectId == project.Id && x.AccountId == existingId)
            .Select(x => x.ProjectRoleId).ToArrayAsync()).Should().BeEquivalentTo(new[] { managerRoleId });
    }

    // 測試案例：TC-ST-MEMBER-005、TC-SQL-006（角色集合完整取代、JWT actor 與前後快照）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:47:48 +08:00
    [Test]
    public async Task 更新成員角色應完整取代為指定集合並記錄前後值()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid targetId, _) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, "角色取代測試");
        Guid roleA = await GetProjectRoleIdAsync(ProjectRoleCodes.FrontendDeveloper);
        Guid roleB = await GetProjectRoleIdAsync(ProjectRoleCodes.BackendDeveloper);
        Guid roleC = await GetProjectRoleIdAsync(ProjectRoleCodes.SystemAnalyst);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);
        (await owner.PostAsJsonAsync($"/api/v1/projects/{project.Id}/members",
            new { accountId = targetId, projectRoleIds = new[] { roleA, roleB } })).StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement updated = await PutJsonAsync(owner, $"/api/v1/projects/{project.Id}/members/{targetId}",
            new { projectRoleIds = new[] { roleB, roleC } });
        updated.GetProperty("roles").EnumerateArray().Select(x => x.GetProperty("id").GetGuid())
            .Should().BeEquivalentTo(new[] { roleB, roleC });

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ProjectMemberRoles.Where(x => x.ProjectId == project.Id && x.AccountId == targetId)
            .Select(x => x.ProjectRoleId).ToArrayAsync()).Should().BeEquivalentTo(new[] { roleB, roleC });
        AuditLog audit = await db.AuditLogs.SingleAsync(x =>
            x.Action == "UpdateMemberRoles" && x.EntityId == project.Id.ToString());
        audit.ActorAccountId.Should().Be(ownerId).And.NotBe(targetId);
        audit.BeforeData.Should().Contain(roleA.ToString()).And.Contain(roleB.ToString());
        audit.AfterData.Should().Contain(roleB.ToString()).And.Contain(roleC.ToString());
        audit.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    // 測試案例：TC-ERR-MEMBER-006（Owner 先移交且新 Owner 自動取得 ProjectManager 後才可移除）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Owner必須先完成移交才能移除且新Owner應具ProjectManager()
    {
        (Guid oldOwnerId, string oldOwnerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid newOwnerId, _) = await CreateUserAsync(SystemRoles.Administrator);
        Project project = await CreateProjectAsync(oldOwnerId, "Owner 移交測試", members: [newOwnerId]);
        Guid managerRoleId = await GetProjectRoleIdAsync(ProjectRoleCodes.ProjectManager);
        Guid memberRoleId = await GetProjectRoleIdAsync(ProjectRoleCodes.Member);
        await using (AsyncServiceScope setupScope = _factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext setupDb = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await setupDb.ProjectMemberRoles.Where(x => x.ProjectId == project.Id && x.AccountId == newOwnerId)
                .ExecuteDeleteAsync();
            setupDb.ProjectMemberRoles.Add(new ProjectMemberRole(project.Id, newOwnerId, memberRoleId));
            await setupDb.SaveChangesAsync();
        }
        using HttpClient owner = await CreateAuthenticatedClientAsync(oldOwnerAccount);

        await AssertProblemAsync(
            await owner.DeleteAsync($"/api/v1/projects/{project.Id}/members/{oldOwnerId}"),
            HttpStatusCode.Conflict,
            "owner_transfer_required");
        JsonElement current = await GetJsonAsync(owner, $"/api/v1/projects/{project.Id}");
        JsonElement transferred = await PutJsonAsync(owner, $"/api/v1/projects/{project.Id}", new
        {
            name = "Owner 已移交",
            description = (string?)null,
            ownerAccountId = newOwnerId,
            timeZoneId = "Asia/Taipei",
            status = ProjectStatus.Active,
            rowVersion = current.GetProperty("rowVersion").GetString()
        });
        transferred.GetProperty("ownerAccountId").GetGuid().Should().Be(newOwnerId);
        (await owner.DeleteAsync($"/api/v1/projects/{project.Id}/members/{oldOwnerId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ProjectMembers.AnyAsync(x => x.ProjectId == project.Id && x.AccountId == oldOwnerId)).Should().BeFalse();
        (await db.ProjectMemberRoles.AnyAsync(x => x.ProjectId == project.Id && x.AccountId == newOwnerId &&
                                                  x.ProjectRoleId == managerRoleId)).Should().BeTrue();
    }

    // 測試案例：TC-ERR-MEMBER-007、TC-SQL-006（失敗無成功 audit；完成後移除記錄 JWT actor）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:47:48 +08:00
    [Test]
    public async Task 有任一未完成Task的成員應被阻擋且完成後才能移除()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);
        foreach (ProjectManagementWeb.Domain.Enums.TaskStatus status in new[]
                 {
                     ProjectManagementWeb.Domain.Enums.TaskStatus.Pending,
                     ProjectManagementWeb.Domain.Enums.TaskStatus.InProgress,
                     ProjectManagementWeb.Domain.Enums.TaskStatus.Blocked
                 })
        {
            (Guid memberId, _) = await CreateUserAsync(SystemRoles.User);
            Project project = await CreateProjectAsync(ownerId, $"移除成員-{status}", members: [memberId]);
            TaskItem task = await CreateTaskAsync(project.Id, ownerId, memberId, status);
            await AssertProblemAsync(
                await owner.DeleteAsync($"/api/v1/projects/{project.Id}/members/{memberId}"),
                HttpStatusCode.Conflict,
                "task_reassignment_required");

            await using (AsyncServiceScope updateScope = _factory.Services.CreateAsyncScope())
            {
                ApplicationDbContext db = updateScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                TaskItem persisted = await db.TaskItems.SingleAsync(x => x.Id == task.Id);
                persisted.UpdateStatus(ProjectManagementWeb.Domain.Enums.TaskStatus.Completed, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync();
            }
            (await owner.DeleteAsync($"/api/v1/projects/{project.Id}/members/{memberId}"))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);

            await using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
            ApplicationDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await verifyDb.ProjectMembers.AnyAsync(x => x.ProjectId == project.Id && x.AccountId == memberId))
                .Should().BeFalse();
            (await verifyDb.ProjectMemberRoles.AnyAsync(x => x.ProjectId == project.Id && x.AccountId == memberId))
                .Should().BeFalse();
            AuditLog audit = await verifyDb.AuditLogs.SingleAsync(x =>
                x.Action == "RemoveMember" && x.EntityId == project.Id.ToString());
            audit.ActorAccountId.Should().Be(ownerId).And.NotBe(memberId);
            audit.BeforeData.Should().Contain(memberId.ToString());
            audit.AfterData.Should().BeNull();
            audit.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        }
    }

    // 測試案例：TC-F-MEMBER-008（成員與多角色讀取、最小欄位及無權 403）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 專案成員讀取應完整回傳多角色且不包含Email()
    {
        (Guid ownerId, string ownerAccount) = await CreateUserAsync(SystemRoles.Administrator);
        (Guid memberId, string memberAccount) = await CreateUserAsync(SystemRoles.User);
        (_, string outsiderAccount) = await CreateUserAsync(SystemRoles.User);
        Project project = await CreateProjectAsync(ownerId, "成員讀取測試");
        Guid frontendRoleId = await GetProjectRoleIdAsync(ProjectRoleCodes.FrontendDeveloper);
        Guid backendRoleId = await GetProjectRoleIdAsync(ProjectRoleCodes.BackendDeveloper);
        using HttpClient owner = await CreateAuthenticatedClientAsync(ownerAccount);
        (await owner.PostAsJsonAsync($"/api/v1/projects/{project.Id}/members",
            new { accountId = memberId, projectRoleIds = new[] { frontendRoleId, backendRoleId } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (string account in new[] { ownerAccount, memberAccount })
        {
            using HttpClient reader = await CreateAuthenticatedClientAsync(account);
            JsonElement members = await GetJsonAsync(reader, $"/api/v1/projects/{project.Id}/members");
            members.GetArrayLength().Should().Be(2);
            JsonElement member = members.EnumerateArray().Single(x => x.GetProperty("accountId").GetGuid() == memberId);
            member.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo("accountId", "account", "name", "roles");
            member.TryGetProperty("email", out _).Should().BeFalse();
            member.GetProperty("roles").EnumerateArray().Select(x => x.GetProperty("id").GetGuid())
                .Should().BeEquivalentTo(new[] { frontendRoleId, backendRoleId });
        }

        using HttpClient outsider = await CreateAuthenticatedClientAsync(outsiderAccount);
        await AssertProblemAsync(
            await outsider.GetAsync($"/api/v1/projects/{project.Id}/members"),
            HttpStatusCode.Forbidden,
            "forbidden");
    }

    private async Task<(Guid Id, string Account)> CreateUserAsync(
        string role, bool enabled = true, string? account = null, bool emailConfirmed = true)
    {
        string suffix = Guid.NewGuid().ToString("N");
        account ??= $"project-api-{role.ToLowerInvariant()}-{suffix}";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = account,
            Email = $"{account}@example.test",
            Name = $"{role} Project 測試帳號",
            EmailConfirmed = emailConfirmed,
            IsEnabled = enabled
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

    private async Task<Project> CreateProjectAsync(
        Guid ownerId,
        string name,
        string? description = null,
        ProjectStatus status = ProjectStatus.Pending,
        DateTimeOffset? createdAt = null,
        IReadOnlyCollection<Guid>? members = null,
        bool deleted = false)
    {
        DateTimeOffset now = createdAt ?? DateTimeOffset.UtcNow;
        var project = new Project(Guid.NewGuid(), $"PRJ-T{Guid.NewGuid():N}"[..20], name, description, ownerId, "Asia/Taipei", now);
        if (status != ProjectStatus.Pending)
        {
            project.Update(name, description, ownerId, "Asia/Taipei", status, now);
        }
        if (deleted)
        {
            project.SoftDelete(ownerId, now.AddSeconds(1));
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Guid managerRoleId = await db.ProjectRoles.Where(x => x.Code == ProjectRoleCodes.ProjectManager)
            .Select(x => x.Id).SingleAsync();
        db.Projects.Add(project);
        db.ProjectMembers.Add(new ProjectMember(project.Id, ownerId, now));
        db.ProjectMemberRoles.Add(new ProjectMemberRole(project.Id, ownerId, managerRoleId));
        foreach (Guid memberId in members ?? [])
        {
            db.ProjectMembers.Add(new ProjectMember(project.Id, memberId, now));
            db.ProjectMemberRoles.Add(new ProjectMemberRole(project.Id, memberId, managerRoleId));
        }
        await db.SaveChangesAsync();
        _projectIds.Add(project.Id);
        return project;
    }

    private async Task<Guid> GetProjectRoleIdAsync(string code)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ProjectRoles.Where(x => x.Code == code).Select(x => x.Id).SingleAsync();
    }

    private async Task<TaskItem> CreateTaskAsync(
        Guid projectId, Guid creatorId, Guid assigneeId, ProjectManagementWeb.Domain.Enums.TaskStatus status)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var task = new TaskItem(
            Guid.NewGuid(), $"TASK-T{Guid.NewGuid():N}"[..22], projectId, creatorId, assigneeId,
            "成員移除測試 Task", null, now, now.AddDays(1), now);
        if (status != ProjectManagementWeb.Domain.Enums.TaskStatus.Pending)
        {
            task.UpdateStatus(status, now);
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

    private static async Task AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedCode)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expectedStatus, body);
        JsonElement problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be(expectedCode);
        problem.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        AssertNoInternalDetails(body);
    }

    private static async Task AssertValidationFieldAsync(HttpResponseMessage response, string field)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonElement problem = JsonDocument.Parse(body).RootElement;
        problem.GetProperty("code").GetString().Should().Be("validation_error");
        problem.GetProperty("errors").TryGetProperty(field, out JsonElement messages).Should().BeTrue();
        messages.GetArrayLength().Should().BeGreaterThan(0);
        problem.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        AssertNoInternalDetails(body);
    }

    private static void AssertNoInternalDetails(string body) => body.Should()
        .NotContain("System.").And.NotContain("Microsoft.Data.SqlClient").And.NotContain("accessToken");

    private async Task CleanupTestDataAsync()
    {
        if (_factory is null)
        {
            return;
        }
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (_taskIds.Count > 0)
        {
            await db.TaskItemComments.IgnoreQueryFilters().Where(x => _taskIds.Contains(x.TaskItemId)).ExecuteDeleteAsync();
            await db.TaskItemHistories.Where(x => _taskIds.Contains(x.TaskItemId)).ExecuteDeleteAsync();
            await db.TaskItems.IgnoreQueryFilters().Where(x => _taskIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
        if (_projectIds.Count > 0)
        {
            string[] projectEntityIds = _projectIds.Select(x => x.ToString()).ToArray();
            await db.AuditLogs.Where(x => x.EntityType == "Project" && projectEntityIds.Contains(x.EntityId))
                .ExecuteDeleteAsync();
            await db.ProjectMemberRoles.Where(x => _projectIds.Contains(x.ProjectId)).ExecuteDeleteAsync();
            await db.ProjectMembers.Where(x => _projectIds.Contains(x.ProjectId)).ExecuteDeleteAsync();
            await db.Projects.IgnoreQueryFilters().Where(x => _projectIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
        if (_accountIds.Count > 0)
        {
            await db.AuditLogs.Where(x => x.ActorAccountId != null && _accountIds.Contains(x.ActorAccountId.Value))
                .ExecuteDeleteAsync();
            await db.RefreshTokens.Where(x => _accountIds.Contains(x.AccountId)).ExecuteDeleteAsync();
            await db.Set<ApplicationUserRole>().Where(x => _accountIds.Contains(x.UserId)).ExecuteDeleteAsync();
            await db.UserPreferences.Where(x => _accountIds.Contains(x.AccountId)).ExecuteDeleteAsync();
            await db.Users.Where(x => _accountIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
    }
}
