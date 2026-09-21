using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
public sealed class UserApiTests
{
    private const string ValidPassword = "Test_password123!";
    private readonly HashSet<Guid> _accountIds = [];
    private readonly HashSet<Guid> _temporarilyDisabledAdminIds = [];
    private TestWebApplicationFactory _factory = null!;
    private string _bootstrapAdminAccount = null!;

    [SetUp]
    public void SetUp()
    {
        string? connectionString = Environment.GetEnvironmentVariable("PMW_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Ignore("未設定 PMW_TEST_SQL_CONNECTION，略過實際 SQL Server User API 測試。");
        }

        _bootstrapAdminAccount = $"bootstrap-test-{Guid.NewGuid():N}";
        _factory = new TestWebApplicationFactory(connectionString, _bootstrapAdminAccount);
    }

    [TearDown]
    public async Task TearDown()
    {
        await RestoreTemporarilyDisabledAdminsAsync();
        await CleanupTestDataAsync();
        await _factory.DisposeAsync();
    }

    // 測試案例：TC-F-USER-001（搜尋 account/name/email、角色篩選、分頁與空結果合併驗證）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Admin查詢使用者應正確套用搜尋角色排序與分頁()
    {
        (_, string adminAccount) = await CreateUserAsync(SystemRoles.Admin);
        string marker = Guid.NewGuid().ToString("N")[..10];
        await CreateUserAsync(SystemRoles.User, account: $"{marker}-charlie", name: $"姓名-{marker}-三");
        (_, string alphaAccount) = await CreateUserAsync(
            SystemRoles.User, account: $"{marker}-alpha", name: $"姓名-{marker}-一");
        (_, string bravoAccount) = await CreateUserAsync(
            SystemRoles.Viewer, account: $"{marker}-bravo", name: $"姓名-{marker}-二");
        using HttpClient admin = await CreateAuthenticatedClientAsync(adminAccount);

        JsonElement firstPage = await GetJsonAsync(
            admin, $"/api/v1/users?search={marker}&page=0&pageSize=2");
        firstPage.GetProperty("page").GetInt32().Should().Be(1);
        firstPage.GetProperty("pageSize").GetInt32().Should().Be(2);
        firstPage.GetProperty("totalCount").GetInt32().Should().Be(3);
        firstPage.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("account").GetString())
            .Should().Equal(alphaAccount, bravoAccount);

        JsonElement secondPage = await GetJsonAsync(
            admin, $"/api/v1/users?search={marker}&page=2&pageSize=2");
        secondPage.GetProperty("items").GetArrayLength().Should().Be(1);

        JsonElement byName = await GetJsonAsync(
            admin, $"/api/v1/users?search={Uri.EscapeDataString($"姓名-{marker}-二")}");
        byName.GetProperty("items").GetArrayLength().Should().Be(1);
        byName.GetProperty("items")[0].GetProperty("account").GetString().Should().Be(bravoAccount);

        JsonElement byEmail = await GetJsonAsync(
            admin, $"/api/v1/users?search={Uri.EscapeDataString($"{marker}-alpha@example.test")}");
        byEmail.GetProperty("items").GetArrayLength().Should().Be(1);
        byEmail.GetProperty("items")[0].GetProperty("account").GetString().Should().Be(alphaAccount);

        JsonElement usersOnly = await GetJsonAsync(
            admin, $"/api/v1/users?search={marker}&role={SystemRoles.User}");
        usersOnly.GetProperty("totalCount").GetInt32().Should().Be(2);
        usersOnly.GetProperty("items").EnumerateArray()
            .Should().OnlyContain(x => x.GetProperty("role").GetString() == SystemRoles.User);

        JsonElement empty = await GetJsonAsync(admin, "/api/v1/users?search=no-such-pmw-user");
        empty.GetProperty("totalCount").GetInt32().Should().Be(0);
        empty.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // 測試案例：TC-ERR-USER-006（Bootstrap Admin 不得出現在使用者清單）
    [Test]
    public async Task 使用者清單應排除BootstrapAdmin且不計入總筆數()
    {
        (_, string adminAccount) = await CreateUserAsync(SystemRoles.Admin);
        (Guid bootstrapId, _) = await CreateUserAsync(SystemRoles.Admin, account: _bootstrapAdminAccount);
        using HttpClient admin = await CreateAuthenticatedClientAsync(adminAccount);

        JsonElement result = await GetJsonAsync(
            admin, $"/api/v1/users?search={Uri.EscapeDataString(_bootstrapAdminAccount)}");

        result.GetProperty("totalCount").GetInt32().Should().Be(0);
        result.GetProperty("items").GetArrayLength().Should().Be(0);
        result.GetProperty("items").EnumerateArray()
            .Should().NotContain(item => item.GetProperty("id").GetGuid() == bootstrapId);
    }

    // 測試案例：TC-ERR-USER-002、TC-F-USER-009（本人／他人／不存在 ID 的 200、403、404 合併驗證）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 使用者詳情應依本人與AccountsRead能力套用已確認的403與404優先序()
    {
        (Guid userId, string userAccount) = await CreateUserAsync(
            SystemRoles.User, name: "一般使用者名稱", phoneNumber: "0912-345-678");
        (Guid viewerId, string viewerAccount) = await CreateUserAsync(
            SystemRoles.Viewer, name: "瀏覽者名稱", phoneNumber: "+886 933-456-789");
        (Guid otherId, _) = await CreateUserAsync(
            SystemRoles.User, name: "他人名稱", phoneNumber: "02-2345-6789");
        (_, string adminAccount) = await CreateUserAsync(SystemRoles.Admin);

        foreach ((Guid ownId, string account, string name, string phoneNumber) in new[]
                 {
                     (userId, userAccount, "一般使用者名稱", "0912-345-678"),
                     (viewerId, viewerAccount, "瀏覽者名稱", "+886 933-456-789")
                 })
        {
            using HttpClient client = await CreateAuthenticatedClientAsync(account);
            await AssertProblemAsync(await client.GetAsync("/api/v1/users"), HttpStatusCode.Forbidden, "forbidden");
            await AssertProblemAsync(
                await client.GetAsync($"/api/v1/users/{otherId}"), HttpStatusCode.Forbidden, "forbidden");
            await AssertProblemAsync(
                await client.GetAsync($"/api/v1/users/{Guid.NewGuid()}"), HttpStatusCode.Forbidden, "forbidden");

            JsonElement own = await GetJsonAsync(client, $"/api/v1/users/{ownId}");
            own.GetProperty("id").GetGuid().Should().Be(ownId);
            own.GetProperty("account").GetString().Should().Be(account);
            own.GetProperty("name").GetString().Should().Be(name);
            own.GetProperty("phoneNumber").GetString().Should().Be(phoneNumber);
            own.GetProperty("emailConfirmed").GetBoolean().Should().BeTrue();
            own.GetProperty("isEnabled").GetBoolean().Should().BeTrue();
            own.GetProperty("isBootstrapAdmin").GetBoolean().Should().BeFalse();
        }

        using HttpClient admin = await CreateAuthenticatedClientAsync(adminAccount);
        JsonElement other = await GetJsonAsync(admin, $"/api/v1/users/{otherId}");
        other.GetProperty("id").GetGuid().Should().Be(otherId);
        other.GetProperty("name").GetString().Should().Be("他人名稱");
        other.GetProperty("phoneNumber").GetString().Should().Be("02-2345-6789");
        await AssertProblemAsync(
            await admin.GetAsync($"/api/v1/users/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "not_found");
    }

    // 測試案例：TC-ST-USER-003、TC-SQL-006（角色與啟用狀態原子更新、JWT actor、token 撤銷與 audit）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:47:48 +08:00
    [Test]
    public async Task Admin原子更新角色與啟用狀態應只增加一次TokenVersion並撤銷Session()
    {
        (Guid adminId, string adminAccount) = await CreateUserAsync(SystemRoles.Admin);
        (Guid targetId, _) = await CreateUserAsync(SystemRoles.User);
        Guid viewerRoleId = await GetRoleIdAsync(SystemRoles.Viewer);
        Guid refreshTokenId = await SeedRefreshTokenAsync(targetId);
        int beforeTokenVersion = await GetTokenVersionAsync(targetId);
        using HttpClient admin = await CreateAuthenticatedClientAsync(adminAccount);

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/api/v1/users/{targetId}/administration",
            new { roleId = viewerRoleId, isEnabled = false });
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonElement result = JsonDocument.Parse(body).RootElement;
        result.GetProperty("role").GetString().Should().Be(SystemRoles.Viewer);
        result.GetProperty("isEnabled").GetBoolean().Should().BeFalse();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ApplicationUser target = await db.Users.SingleAsync(x => x.Id == targetId);
        target.TokenVersion.Should().Be(beforeTokenVersion + 1);
        target.IsEnabled.Should().BeFalse();
        ApplicationUserRole role = await db.Set<ApplicationUserRole>().SingleAsync(x => x.UserId == targetId);
        role.RoleId.Should().Be(viewerRoleId);
        (await db.RefreshTokens.SingleAsync(x => x.Id == refreshTokenId)).RevokedAt.Should().NotBeNull();
        AuditLog audit = await db.AuditLogs.SingleAsync(x =>
            x.ActorAccountId == adminId && x.Action == "UpdateAdministration" && x.EntityId == targetId.ToString());
        audit.BeforeData.Should().Contain(SystemRoles.User).And.Contain("true");
        audit.AfterData.Should().Contain(SystemRoles.Viewer).And.Contain("false");
        audit.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    // 測試案例：TC-F-PREF-001、TC-ERR-PREF-002（帳號隔離、持久化與 Viewer 唯讀合併驗證）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 個人偏好應按帳號持久化且Viewer只能讀取()
    {
        (_, string accountA) = await CreateUserAsync(SystemRoles.User);
        (_, string accountB) = await CreateUserAsync(SystemRoles.User);
        (_, string viewerAccount) = await CreateUserAsync(SystemRoles.Viewer);
        using HttpClient userA = await CreateAuthenticatedClientAsync(accountA);
        using HttpClient userB = await CreateAuthenticatedClientAsync(accountB);
        using HttpClient viewer = await CreateAuthenticatedClientAsync(viewerAccount);

        JsonElement initialA = await GetJsonAsync(userA, "/api/v1/users/me/preferences");
        JsonElement initialB = await GetJsonAsync(userB, "/api/v1/users/me/preferences");
        initialA.GetProperty("skipBatchConfirmation").GetBoolean().Should().BeFalse();
        initialB.GetProperty("skipBatchConfirmation").GetBoolean().Should().BeFalse();

        JsonElement updatedA = await PutJsonAsync(
            userA, "/api/v1/users/me/preferences", new { skipBatchConfirmation = true });
        updatedA.GetProperty("skipBatchConfirmation").GetBoolean().Should().BeTrue();
        (await GetJsonAsync(userB, "/api/v1/users/me/preferences"))
            .GetProperty("skipBatchConfirmation").GetBoolean().Should().BeFalse();

        using HttpClient reauthenticatedA = await CreateAuthenticatedClientAsync(accountA);
        (await GetJsonAsync(reauthenticatedA, "/api/v1/users/me/preferences"))
            .GetProperty("skipBatchConfirmation").GetBoolean().Should().BeTrue();
        (await PutJsonAsync(reauthenticatedA, "/api/v1/users/me/preferences", new { skipBatchConfirmation = false }))
            .GetProperty("skipBatchConfirmation").GetBoolean().Should().BeFalse();

        (await GetJsonAsync(viewer, "/api/v1/users/me/preferences"))
            .GetProperty("skipBatchConfirmation").GetBoolean().Should().BeFalse();
        await AssertProblemAsync(
            await viewer.PutAsJsonAsync("/api/v1/users/me/preferences", new { skipBatchConfirmation = true }),
            HttpStatusCode.Forbidden,
            "forbidden");
    }

    // 測試案例：TC-ERR-USER-004（未驗證 Viewer 對三種提升角色與維持 Viewer 合併驗證）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 未驗證帳號不得提升角色但可維持Viewer並更新狀態()
    {
        (_, string adminAccount) = await CreateUserAsync(SystemRoles.Admin);
        (Guid targetId, _) = await CreateUserAsync(SystemRoles.Viewer, emailConfirmed: false);
        using HttpClient admin = await CreateAuthenticatedClientAsync(adminAccount);
        int beforeVersion = await GetTokenVersionAsync(targetId);

        foreach (string prohibitedRole in new[] { SystemRoles.User, SystemRoles.Administrator, SystemRoles.Admin })
        {
            Guid roleId = await GetRoleIdAsync(prohibitedRole);
            await AssertProblemAsync(
                await admin.PutAsJsonAsync(
                    $"/api/v1/users/{targetId}/administration",
                    new { roleId, isEnabled = true }),
                HttpStatusCode.UnprocessableEntity,
                "email_not_confirmed");
        }

        (string Role, bool Enabled, int TokenVersion) unchanged = await GetUserStateAsync(targetId);
        unchanged.Should().Be((SystemRoles.Viewer, true, beforeVersion));

        Guid viewerRoleId = await GetRoleIdAsync(SystemRoles.Viewer);
        JsonElement response = await PutJsonAsync(
            admin,
            $"/api/v1/users/{targetId}/administration",
            new { roleId = viewerRoleId, isEnabled = false });
        response.GetProperty("role").GetString().Should().Be(SystemRoles.Viewer);
        response.GetProperty("isEnabled").GetBoolean().Should().BeFalse();
    }

    // 測試案例：TC-ERR-USER-006（Bootstrap Admin 三個帳號管理 API 保護；Project membership 另批驗證）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task BootstrapAdmin透過三個帳號管理Api皆不可修改()
    {
        (Guid actorId, string adminAccount) = await CreateUserAsync(SystemRoles.Admin);
        (Guid bootstrapId, _) = await CreateUserAsync(SystemRoles.Admin, account: _bootstrapAdminAccount);
        using HttpClient admin = await CreateAuthenticatedClientAsync(adminAccount);
        Guid userRoleId = await GetRoleIdAsync(SystemRoles.User);

        await AssertProblemAsync(
            await admin.PutAsJsonAsync($"/api/v1/users/{bootstrapId}/role", new { roleId = userRoleId }),
            HttpStatusCode.Conflict,
            "bootstrap_admin_immutable");
        await AssertProblemAsync(
            await admin.PatchAsJsonAsync($"/api/v1/users/{bootstrapId}/status", new { isEnabled = false }),
            HttpStatusCode.Conflict,
            "bootstrap_admin_immutable");
        await AssertProblemAsync(
            await admin.PutAsJsonAsync(
                $"/api/v1/users/{bootstrapId}/administration",
                new { roleId = userRoleId, isEnabled = false }),
            HttpStatusCode.Conflict,
            "bootstrap_admin_immutable");

        (await GetUserStateAsync(bootstrapId)).Should().Be((SystemRoles.Admin, true, 0));
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.AuditLogs.CountAsync(x => x.ActorAccountId == actorId && x.EntityId == bootstrapId.ToString()))
            .Should().Be(0);
    }

    // 測試案例：TC-F-USER-007（分離 role/status API 的 token 撤銷、版本與 audit）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 分離角色與狀態Api應只改指定面向並各自使Session失效()
    {
        (Guid adminId, string adminAccount) = await CreateUserAsync(SystemRoles.Admin);
        (Guid targetId, _) = await CreateUserAsync(SystemRoles.User);
        using HttpClient admin = await CreateAuthenticatedClientAsync(adminAccount);
        Guid viewerRoleId = await GetRoleIdAsync(SystemRoles.Viewer);
        Guid firstTokenId = await SeedRefreshTokenAsync(targetId);

        JsonElement roleResponse = await PutJsonAsync(
            admin, $"/api/v1/users/{targetId}/role", new { roleId = viewerRoleId });
        roleResponse.GetProperty("role").GetString().Should().Be(SystemRoles.Viewer);
        roleResponse.GetProperty("isEnabled").GetBoolean().Should().BeTrue();
        (await GetUserStateAsync(targetId)).Should().Be((SystemRoles.Viewer, true, 1));
        (await GetRefreshTokenAsync(firstTokenId)).RevokedAt.Should().NotBeNull();

        Guid secondTokenId = await SeedRefreshTokenAsync(targetId);
        HttpResponseMessage statusResponse = await admin.PatchAsJsonAsync(
            $"/api/v1/users/{targetId}/status", new { isEnabled = false });
        string statusBody = await statusResponse.Content.ReadAsStringAsync();
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK, statusBody);
        JsonElement statusResult = JsonDocument.Parse(statusBody).RootElement;
        statusResult.GetProperty("role").GetString().Should().Be(SystemRoles.Viewer);
        statusResult.GetProperty("isEnabled").GetBoolean().Should().BeFalse();
        (await GetUserStateAsync(targetId)).Should().Be((SystemRoles.Viewer, false, 2));
        (await GetRefreshTokenAsync(secondTokenId)).RevokedAt.Should().NotBeNull();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        string[] actions = await db.AuditLogs
            .Where(x => x.ActorAccountId == adminId && x.EntityId == targetId.ToString())
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Action)
            .ToArrayAsync();
        actions.Should().Equal("ReplaceRole", "UpdateStatus");
    }

    // 測試案例：TC-ERR-USER-010（非 Admin、目標不存在與角色不存在的失敗矩陣合併驗證）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 帳號管理失敗矩陣應維持403與404契約且不異動目標資料()
    {
        (Guid targetId, _) = await CreateUserAsync(SystemRoles.User);
        Guid viewerRoleId = await GetRoleIdAsync(SystemRoles.Viewer);
        Guid targetTokenId = await SeedRefreshTokenAsync(targetId);
        (Guid adminId, string adminAccount) = await CreateUserAsync(SystemRoles.Admin);
        var nonAdminAccounts = new List<string>();
        foreach (string role in new[] { SystemRoles.User, SystemRoles.Viewer, SystemRoles.Administrator })
        {
            (_, string account) = await CreateUserAsync(role);
            nonAdminAccounts.Add(account);
        }

        foreach (string account in nonAdminAccounts)
        {
            using HttpClient client = await CreateAuthenticatedClientAsync(account);
            foreach (Guid id in new[] { targetId, Guid.NewGuid() })
            {
                await AssertProblemAsync(
                    await client.PutAsJsonAsync($"/api/v1/users/{id}/role", new { roleId = viewerRoleId }),
                    HttpStatusCode.Forbidden,
                    "forbidden");
                await AssertProblemAsync(
                    await client.PatchAsJsonAsync($"/api/v1/users/{id}/status", new { isEnabled = false }),
                    HttpStatusCode.Forbidden,
                    "forbidden");
                await AssertProblemAsync(
                    await client.PutAsJsonAsync(
                        $"/api/v1/users/{id}/administration",
                        new { roleId = viewerRoleId, isEnabled = false }),
                    HttpStatusCode.Forbidden,
                    "forbidden");
            }
        }

        using HttpClient admin = await CreateAuthenticatedClientAsync(adminAccount);
        Guid missingUserId = Guid.NewGuid();
        await AssertProblemAsync(
            await admin.PutAsJsonAsync($"/api/v1/users/{missingUserId}/role", new { roleId = viewerRoleId }),
            HttpStatusCode.NotFound,
            "not_found");
        await AssertProblemAsync(
            await admin.PatchAsJsonAsync($"/api/v1/users/{missingUserId}/status", new { isEnabled = false }),
            HttpStatusCode.NotFound,
            "not_found");
        await AssertProblemAsync(
            await admin.PutAsJsonAsync(
                $"/api/v1/users/{missingUserId}/administration",
                new { roleId = viewerRoleId, isEnabled = false }),
            HttpStatusCode.NotFound,
            "not_found");

        Guid missingRoleId = Guid.NewGuid();
        await AssertProblemAsync(
            await admin.PutAsJsonAsync($"/api/v1/users/{targetId}/role", new { roleId = missingRoleId }),
            HttpStatusCode.NotFound,
            "not_found");
        await AssertProblemAsync(
            await admin.PutAsJsonAsync(
                $"/api/v1/users/{targetId}/administration",
                new { roleId = missingRoleId, isEnabled = false }),
            HttpStatusCode.NotFound,
            "not_found");

        (await GetUserStateAsync(targetId)).Should().Be((SystemRoles.User, true, 0));
        (await GetRefreshTokenAsync(targetTokenId)).RevokedAt.Should().BeNull();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.AuditLogs.CountAsync(x => x.ActorAccountId == adminId && x.EntityId == targetId.ToString()))
            .Should().Be(0);
    }

    [Test]
    public async Task 登入使用者可讀取修改並清除自己的名稱與電話且不影響他人()
    {
        (Guid accountId, string account) = await CreateUserAsync(
            SystemRoles.Viewer, name: "原始名稱");
        (Guid otherId, _) = await CreateUserAsync(SystemRoles.User, name: "其他使用者");
        await SetPhoneNumberConfirmedAsync(accountId);
        using HttpClient client = await CreateAuthenticatedClientAsync(account);

        JsonElement initial = await GetJsonAsync(client, "/api/v1/users/me/profile");
        initial.GetProperty("name").GetString().Should().Be("原始名稱");
        initial.GetProperty("phoneNumber").ValueKind.Should().Be(JsonValueKind.Null);

        JsonElement updated = await PutJsonAsync(
            client,
            "/api/v1/users/me/profile",
            new { name = "  更新後名稱  ", phoneNumber = "  +886 912-345-678  " });
        updated.GetProperty("name").GetString().Should().Be("更新後名稱");
        updated.GetProperty("phoneNumber").GetString().Should().Be("+886 912-345-678");

        JsonElement currentAccount = await GetJsonAsync(client, "/api/v1/auth/me");
        currentAccount.GetProperty("name").GetString().Should().Be("更新後名稱");
        JsonElement cleared = await PutJsonAsync(
            client,
            "/api/v1/users/me/profile",
            new { name = "更新後名稱", phoneNumber = "   " });
        cleared.GetProperty("phoneNumber").ValueKind.Should().Be(JsonValueKind.Null);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ApplicationUser accountUser = await db.Users.AsNoTracking().SingleAsync(x => x.Id == accountId);
        accountUser.Name.Should().Be("更新後名稱");
        accountUser.PhoneNumber.Should().BeNull();
        accountUser.PhoneNumberConfirmed.Should().BeFalse();
        ApplicationUser otherUser = await db.Users.AsNoTracking().SingleAsync(x => x.Id == otherId);
        otherUser.Name.Should().Be("其他使用者");
        otherUser.PhoneNumber.Should().BeNull();

        AuditLog[] audits = await db.AuditLogs.AsNoTracking()
            .Where(x => x.ActorAccountId == accountId && x.Action == "UpdateOwnProfile")
            .OrderBy(x => x.CreatedAt)
            .ToArrayAsync();
        audits.Should().HaveCount(2);
        audits[0].AfterData.Should().Contain("name").And.Contain("phoneNumber");
        audits.Select(x => x.AfterData).Should().NotContain(value =>
            value!.Contains("更新後名稱", StringComparison.Ordinal) ||
            value.Contains("+886 912-345-678", StringComparison.Ordinal));
    }

    [Test]
    public async Task 個人資料更新應拒絕空白過長名稱及無效電話並保持原資料()
    {
        (Guid accountId, string account) = await CreateUserAsync(SystemRoles.User, name: "原始名稱");
        using HttpClient client = await CreateAuthenticatedClientAsync(account);

        foreach ((object Body, string Field) invalid in new (object, string)[]
                 {
                     (new { name = "   ", phoneNumber = (string?)null }, "name"),
                     (new { name = new string('名', 101), phoneNumber = (string?)null }, "name"),
                     (new { name = "有效名稱", phoneNumber = "invalid-phone" }, "phoneNumber"),
                     (new { name = "有效名稱", phoneNumber = new string('1', 31) }, "phoneNumber")
                 })
        {
            HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/users/me/profile", invalid.Body);
            string body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            JsonElement problem = JsonDocument.Parse(body).RootElement;
            problem.GetProperty("code").GetString().Should().Be("validation_error");
            problem.GetProperty("errors").TryGetProperty(invalid.Field, out JsonElement messages).Should().BeTrue();
            messages.GetArrayLength().Should().BeGreaterThan(0);
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ApplicationUser accountUser = await db.Users.AsNoTracking().SingleAsync(x => x.Id == accountId);
        accountUser.Name.Should().Be("原始名稱");
        accountUser.PhoneNumber.Should().BeNull();
        (await db.AuditLogs.CountAsync(x =>
            x.ActorAccountId == accountId && x.Action == "UpdateOwnProfile")).Should().Be(0);
    }

    [Test]
    public async Task OpenApi應只開放名稱電話角色狀態與偏好等已確認的使用者異動欄位()
    {
        using HttpClient client = _factory.CreateClient();
        JsonElement openApi = await GetJsonAsync(client, "/openapi/v1.json");
        JsonElement schemas = openApi.GetProperty("components").GetProperty("schemas");

        schemas.GetProperty("UpdateRoleRequest").GetProperty("properties").EnumerateObject()
            .Select(x => x.Name).Should().Equal("roleId");
        schemas.GetProperty("UpdateUserStatusRequest").GetProperty("properties").EnumerateObject()
            .Select(x => x.Name).Should().Equal("isEnabled");
        schemas.GetProperty("UpdateAdministrationRequest").GetProperty("properties").EnumerateObject()
            .Select(x => x.Name).Should().BeEquivalentTo("roleId", "isEnabled");
        schemas.GetProperty("UpdatePreferenceRequest").GetProperty("properties").EnumerateObject()
            .Select(x => x.Name).Should().Equal("skipBatchConfirmation");
        schemas.GetProperty("UpdateOwnProfileRequest").GetProperty("properties").EnumerateObject()
            .Select(x => x.Name).Should().BeEquivalentTo("name", "phoneNumber");
    }

    // 測試案例：TC-ERR-USER-005（最後一位 Admin 的降級、停用與兩請求並行競態）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 最後一位有效Admin不得降級停用且並行異動後仍至少保留一位()
    {
        (Guid soleAdminId, string soleAdminAccount) = await CreateUserAsync(SystemRoles.Admin);
        using HttpClient soleAdmin = await CreateAuthenticatedClientAsync(soleAdminAccount);
        await DisableOtherAdminsAsync([soleAdminId]);
        Guid userRoleId = await GetRoleIdAsync(SystemRoles.User);

        await AssertProblemAsync(
            await soleAdmin.PutAsJsonAsync($"/api/v1/users/{soleAdminId}/role", new { roleId = userRoleId }),
            HttpStatusCode.Conflict,
            "last_admin");
        await AssertProblemAsync(
            await soleAdmin.PatchAsJsonAsync($"/api/v1/users/{soleAdminId}/status", new { isEnabled = false }),
            HttpStatusCode.Conflict,
            "last_admin");
        await AssertProblemAsync(
            await soleAdmin.PutAsJsonAsync(
                $"/api/v1/users/{soleAdminId}/administration",
                new { roleId = userRoleId, isEnabled = false }),
            HttpStatusCode.Conflict,
            "last_admin");
        (await GetUserStateAsync(soleAdminId)).Should().Be((SystemRoles.Admin, true, 0));

        (Guid adminAId, string adminAAccount) = await CreateUserAsync(SystemRoles.Admin);
        (Guid adminBId, string adminBAccount) = await CreateUserAsync(SystemRoles.Admin);
        using HttpClient adminA = await CreateAuthenticatedClientAsync(adminAAccount);
        using HttpClient adminB = await CreateAuthenticatedClientAsync(adminBAccount);
        await DisableOtherAdminsAsync([adminAId, adminBId]);

        Task<HttpResponseMessage> requestA = adminA.PutAsJsonAsync(
            $"/api/v1/users/{adminAId}/administration", new { roleId = userRoleId, isEnabled = true });
        Task<HttpResponseMessage> requestB = adminB.PutAsJsonAsync(
            $"/api/v1/users/{adminBId}/administration", new { roleId = userRoleId, isEnabled = true });
        HttpResponseMessage[] responses = await Task.WhenAll(requestA, requestB);
        responses.Select(x => x.StatusCode).Should().BeEquivalentTo(
            [HttpStatusCode.OK, HttpStatusCode.Conflict]);
        HttpResponseMessage conflict = responses.Single(x => x.StatusCode == HttpStatusCode.Conflict);
        await AssertProblemAsync(conflict, HttpStatusCode.Conflict, "last_admin");
        (await CountEnabledAdminsAsync()).Should().Be(1);
    }

    // 測試案例：TC-F-API-002（固定角色、Project Role 與完整 Function mapping 的 API／SQL read-back）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 固定角色與FunctionMapping應從實際資料庫完整回傳()
    {
        (_, string adminAccount) = await CreateUserAsync(SystemRoles.Admin);
        using HttpClient admin = await CreateAuthenticatedClientAsync(adminAccount);

        JsonElement roles = await GetJsonAsync(admin, "/api/v1/roles");
        Dictionary<string, string[]> functionsByRole = roles.EnumerateArray().ToDictionary(
            role => role.GetProperty("name").GetString()!,
            role => role.GetProperty("functions").EnumerateArray().Select(x => x.GetString()!).ToArray());
        functionsByRole.Keys.Should().BeEquivalentTo(SystemRoles.All);
        foreach (string role in SystemRoles.All)
        {
            functionsByRole[role].Should().BeEquivalentTo(GetExpectedFunctions(role));
        }

        JsonElement projectRoles = await GetJsonAsync(admin, "/api/v1/projects/roles");
        projectRoles.EnumerateArray().Select(x => x.GetProperty("code").GetString())
            .Should().BeEquivalentTo(
                ProjectRoleCodes.ProjectManager,
                ProjectRoleCodes.FrontendDeveloper,
                ProjectRoleCodes.BackendDeveloper,
                ProjectRoleCodes.SystemAnalyst,
                ProjectRoleCodes.Member);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.Roles.CountAsync()).Should().Be(4);
        (await db.ProjectRoles.CountAsync()).Should().Be(5);
        (await db.Functions.CountAsync()).Should().Be(SystemFunctions.All.Count);
    }

    private async Task<(Guid Id, string Account)> CreateUserAsync(
        string role, bool emailConfirmed = true, bool enabled = true, string? account = null,
        string? name = null, string? phoneNumber = null)
    {
        string suffix = Guid.NewGuid().ToString("N");
        account ??= $"user-api-{role.ToLowerInvariant()}-{suffix}";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = account,
            Email = $"{account}@example.test",
            Name = name ?? $"{role} API 測試帳號",
            PhoneNumber = phoneNumber,
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
        string accessToken = JsonDocument.Parse(body).RootElement.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<Guid> GetRoleIdAsync(string role)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        RoleManager<ApplicationRole> roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        ApplicationRole? applicationRole = await roleManager.FindByNameAsync(role);
        applicationRole.Should().NotBeNull();
        return applicationRole!.Id;
    }

    private async Task<Guid> SeedRefreshTokenAsync(Guid accountId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var token = new RefreshToken(
            Guid.NewGuid(),
            accountId,
            Guid.NewGuid(),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")))),
            now,
            now.AddHours(1));
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync();
        return token.Id;
    }

    private async Task<int> GetTokenVersionAsync(Guid accountId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Users.Where(x => x.Id == accountId).Select(x => x.TokenVersion).SingleAsync();
    }

    private async Task SetPhoneNumberConfirmedAsync(Guid accountId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Users.Where(x => x.Id == accountId).ExecuteUpdateAsync(
            updates => updates.SetProperty(x => x.PhoneNumberConfirmed, true));
    }

    private async Task<(string Role, bool Enabled, int TokenVersion)> GetUserStateAsync(Guid accountId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await (from user in db.Users
                      join userRole in db.Set<ApplicationUserRole>() on user.Id equals userRole.UserId
                      join role in db.Roles on userRole.RoleId equals role.Id
                      where user.Id == accountId
                      select new ValueTuple<string, bool, int>(role.Name!, user.IsEnabled, user.TokenVersion))
            .SingleAsync();
    }

    private async Task<RefreshToken> GetRefreshTokenAsync(Guid tokenId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.RefreshTokens.AsNoTracking().SingleAsync(x => x.Id == tokenId);
    }

    private async Task DisableOtherAdminsAsync(IReadOnlyCollection<Guid> keepEnabledIds)
    {
        Guid adminRoleId = await GetRoleIdAsync(SystemRoles.Admin);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Guid[] ids = await (from user in db.Users
                            join userRole in db.Set<ApplicationUserRole>() on user.Id equals userRole.UserId
                            where userRole.RoleId == adminRoleId && user.IsEnabled && !keepEnabledIds.Contains(user.Id)
                            select user.Id).ToArrayAsync();
        if (ids.Length == 0)
        {
            return;
        }

        await db.Users.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(
            updates => updates.SetProperty(x => x.IsEnabled, false));
        _temporarilyDisabledAdminIds.UnionWith(ids);
    }

    private async Task<int> CountEnabledAdminsAsync()
    {
        Guid adminRoleId = await GetRoleIdAsync(SystemRoles.Admin);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await (from user in db.Users
                      join userRole in db.Set<ApplicationUserRole>() on user.Id equals userRole.UserId
                      where userRole.RoleId == adminRoleId && user.IsEnabled
                      select user.Id).CountAsync();
    }

    private async Task RestoreTemporarilyDisabledAdminsAsync()
    {
        if (_factory is null || _temporarilyDisabledAdminIds.Count == 0)
        {
            return;
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Users.Where(x => _temporarilyDisabledAdminIds.Contains(x.Id)).ExecuteUpdateAsync(
            updates => updates.SetProperty(x => x.IsEnabled, true));
        _temporarilyDisabledAdminIds.Clear();
    }

    private static IReadOnlyCollection<string> GetExpectedFunctions(string role) => role switch
    {
        SystemRoles.Admin => SystemFunctions.All,
        SystemRoles.Administrator =>
        [
            SystemFunctions.AccountsRead, SystemFunctions.ProjectsRead, SystemFunctions.ProjectsCreate,
            SystemFunctions.ProjectsManageAll, SystemFunctions.ProjectMembersManageAll, SystemFunctions.TasksRead,
            SystemFunctions.TasksCreate, SystemFunctions.TasksUpdateAny, SystemFunctions.TasksDelete,
            SystemFunctions.CommentsRead, SystemFunctions.CommentsCreate, SystemFunctions.CommentsUpdateOwn,
            SystemFunctions.CommentsDeleteOwn, SystemFunctions.PreferencesReadOwn, SystemFunctions.PreferencesUpdateOwn
        ],
        SystemRoles.User =>
        [
            SystemFunctions.ProjectsRead, SystemFunctions.TasksRead, SystemFunctions.TasksUpdateAssigned,
            SystemFunctions.CommentsRead, SystemFunctions.CommentsCreate, SystemFunctions.CommentsUpdateOwn,
            SystemFunctions.CommentsDeleteOwn, SystemFunctions.PreferencesReadOwn, SystemFunctions.PreferencesUpdateOwn
        ],
        SystemRoles.Viewer =>
        [
            SystemFunctions.ProjectsRead, SystemFunctions.TasksRead, SystemFunctions.CommentsRead,
            SystemFunctions.PreferencesReadOwn
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未知系統角色。")
    };

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> PutJsonAsync(HttpClient client, string path, object bodyValue)
    {
        HttpResponseMessage response = await client.PutAsJsonAsync(path, bodyValue);
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
        body.Should().NotContain("System.").And.NotContain("Microsoft.Data.SqlClient").And.NotContain("accessToken");
    }

    private async Task CleanupTestDataAsync()
    {
        if (_factory is null || _accountIds.Count == 0)
        {
            return;
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        string[] entityIds = _accountIds.Select(x => x.ToString()).ToArray();
        await db.AuditLogs.Where(x =>
            (x.ActorAccountId != null && _accountIds.Contains(x.ActorAccountId.Value)) ||
            (x.EntityType == "Account" && entityIds.Contains(x.EntityId))).ExecuteDeleteAsync();
        await db.RefreshTokens.Where(x => _accountIds.Contains(x.AccountId)).ExecuteDeleteAsync();
        await db.Set<ApplicationUserRole>().Where(x => _accountIds.Contains(x.UserId)).ExecuteDeleteAsync();
        await db.UserPreferences.Where(x => _accountIds.Contains(x.AccountId)).ExecuteDeleteAsync();
        await db.Users.Where(x => _accountIds.Contains(x.Id)).ExecuteDeleteAsync();
        _accountIds.Clear();
    }
}
