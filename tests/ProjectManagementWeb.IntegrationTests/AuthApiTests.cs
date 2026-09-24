using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Enums;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.IntegrationTests;

public sealed class AuthApiTests
{
    private const string ValidPassword = "Test_password123!";
    private string _testPrivateKeyPem = null!;
    private TestWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private TestTimeProvider _timeProvider = null!;
    private readonly HashSet<Guid> _accountIds = [];
    private readonly HashSet<string> _emails = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loginAccountKeyHashes = new(StringComparer.Ordinal);

    [SetUp]
    public void SetUp()
    {
        string? connectionString = Environment.GetEnvironmentVariable("PMW_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Ignore("未設定 PMW_TEST_SQL_CONNECTION，略過實際 SQL Server Auth API 測試。");
        }

        _testPrivateKeyPem = CreateTestPrivateKeyPem();
        _timeProvider = new TestTimeProvider(DateTimeOffset.UtcNow);
        _factory = new TestWebApplicationFactory(
            connectionString,
            jwtPrivateKeyPem: _testPrivateKeyPem,
            timeProvider: _timeProvider);
        _client = _factory.CreateClient();
        _factory.EmailGateway.Reset();
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.Dispose();
        await CleanupTestDataAsync();
        await _factory.DisposeAsync();
    }

    // 測試案例：TC-F-AUTH-001（API、SQL 與 fake Email gateway）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 註冊有效帳號應建立Viewer偏好與成功郵件紀錄()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string account = $"register-{suffix}";
        string email = $"register-{suffix}@example.test";

        HttpResponseMessage response = await RegisterAsync(account, email);
        string body = await response.Content.ReadAsStringAsync();
        JsonElement result = JsonDocument.Parse(body).RootElement;
        Guid accountId = result.GetProperty("accountId").GetGuid();
        Track(accountId, email);

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        result.GetProperty("verificationEmailSent").GetBoolean().Should().BeTrue();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await db.Users.SingleAsync(x => x.Id == accountId);
        string[] roles = [.. await userManager.GetRolesAsync(user)];

        user.UserName.Should().Be(account);
        user.Email.Should().Be(email);
        user.EmailConfirmed.Should().BeFalse();
        user.IsEnabled.Should().BeTrue();
        user.PasswordHash.Should().NotBeNullOrWhiteSpace().And.NotBe(ValidPassword);
        roles.Should().Equal(SystemRoles.Viewer);
        (await db.UserPreferences.AnyAsync(x => x.AccountId == accountId)).Should().BeTrue();
        var message = await db.EmailMessages.SingleAsync(x => x.Recipient == email);
        message.Status.Should().Be(EmailDeliveryStatus.Sent);
        message.AttemptCount.Should().Be(1);
        _factory.EmailGateway.Recipients.Should().Equal(email);
    }

    // 測試案例：TC-E-AUTH-002（註冊欄位 API／SQL 邊界）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 22:14:40 +08:00
    [Test]
    public async Task 註冊Api應與Unit及Frontend使用相同欄位邊界且拒絕值不建立資料()
    {
        string suffix = Guid.NewGuid().ToString("N")[..10];
        (int Users, int Preferences, int Tokens, int Emails) before = await GetAuthDataCountsAsync();
        foreach ((string account, string password, string confirmPassword, string email, string name, string field) in new[]
                 {
                     (new string('a', 257), "Aa1!aaaaaa", "Aa1!aaaaaa", $"account-long-{suffix}@example.test", "使用者", "account"),
                     ($"name-empty-{suffix}", "Aa1!aaaaaa", "Aa1!aaaaaa", $"name-empty-{suffix}@example.test", "   ", "name"),
                     ($"name-long-{suffix}", "Aa1!aaaaaa", "Aa1!aaaaaa", $"name-long-{suffix}@example.test", new string('名', 101), "name"),
                     ($"password-short-{suffix}", "Aa1!aaaaa", "Aa1!aaaaa", $"password-short-{suffix}@example.test", "使用者", "password")
                 })
        {
            HttpResponseMessage response = await SendRegistrationAsync(account, password, confirmPassword, email, name);
            string body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            JsonElement problem = JsonDocument.Parse(body).RootElement;
            problem.GetProperty("code").GetString().Should().Be("validation_error");
            problem.GetProperty("errors").TryGetProperty(field, out JsonElement messages).Should().BeTrue();
            messages.GetArrayLength().Should().BeGreaterThan(0);
        }
        (await GetAuthDataCountsAsync()).Should().Be(before, "欄位驗證失敗不得建立任何 Auth 資料");

        string accountAtLimit = $"limit-{suffix}-".PadRight(256, 'a');
        foreach ((string account, string email, string name) in new[]
                 {
                     (accountAtLimit, $"account-limit-{suffix}@example.test", "名"),
                     ($"a-._@+{suffix}", $"symbols-{suffix}@example.test", new string('名', 100))
                 })
        {
            HttpResponseMessage response = await SendRegistrationAsync(
                account,
                "Aa1!aaaaaa",
                "Aa1!aaaaaa",
                email,
                name);
            string body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Created, body);
            Guid accountId = JsonDocument.Parse(body).RootElement.GetProperty("accountId").GetGuid();
            Track(accountId, email);

            await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ApplicationUser saved = await db.Users.AsNoTracking().SingleAsync(x => x.Id == accountId);
            saved.UserName.Should().Be(account);
            saved.Name.Should().Be(name);
        }
    }

    // 測試案例：TC-ST-AUTH-006（SMTP gateway 失敗狀態）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task EmailGateway失敗時註冊仍應保留Viewer並記錄失敗郵件()
    {
        _factory.EmailGateway.ShouldFail = true;
        string suffix = Guid.NewGuid().ToString("N");
        string email = $"mail-failed-{suffix}@example.test";

        HttpResponseMessage response = await RegisterAsync($"mail-failed-{suffix}", email);
        string body = await response.Content.ReadAsStringAsync();
        JsonElement result = JsonDocument.Parse(body).RootElement;
        Guid accountId = result.GetProperty("accountId").GetGuid();
        Track(accountId, email);

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        result.GetProperty("verificationEmailSent").GetBoolean().Should().BeFalse();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await db.Users.SingleAsync(x => x.Id == accountId);
        string[] roles = [.. await userManager.GetRolesAsync(user)];
        var message = await db.EmailMessages.SingleAsync(x => x.Recipient == email);

        user.IsEnabled.Should().BeTrue();
        roles.Should().Equal(SystemRoles.Viewer);
        message.Status.Should().Be(EmailDeliveryStatus.Failed);
        message.AttemptCount.Should().Be(1);
        message.LastError.Should().Be("測試用 Email gateway 失敗。");
    }

    // 測試案例：TC-ERR-AUTH-004（相同帳號與相同 Email 合併驗證）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 重複帳號或Email應回欄位錯誤且不建立孤兒資料()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string account = $"duplicate-{suffix}";
        string email = $"duplicate-{suffix}@example.test";
        HttpResponseMessage original = await RegisterAsync(account, email);
        JsonElement originalResult = JsonDocument.Parse(await original.Content.ReadAsStringAsync()).RootElement;
        Guid originalAccountId = originalResult.GetProperty("accountId").GetGuid();
        Track(originalAccountId, email);

        HttpResponseMessage duplicateAccount = await RegisterAsync(account, $"other-{suffix}@example.test");
        HttpResponseMessage duplicateEmail = await RegisterAsync($"other-{suffix}", email);

        await AssertRegistrationFieldErrorAsync(duplicateAccount, "account");
        await AssertProblemWithFieldErrorAsync(
            duplicateEmail,
            HttpStatusCode.Conflict,
            "duplicate_email",
            "email");

        string parallelEmail = $"parallel-{suffix}@example.test";
        HttpResponseMessage[] parallelResponses = await Task.WhenAll(
            RegisterAsync($"parallel-a-{suffix}", parallelEmail),
            RegisterAsync($"parallel-b-{suffix}", parallelEmail));
        parallelResponses.Count(response => response.StatusCode == HttpStatusCode.Created).Should().Be(1);
        parallelResponses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        HttpResponseMessage conflict = parallelResponses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
        await AssertProblemWithFieldErrorAsync(conflict, HttpStatusCode.Conflict, "duplicate_email", "email");
        HttpResponseMessage created = parallelResponses.Single(response => response.StatusCode == HttpStatusCode.Created);
        Guid parallelAccountId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("accountId").GetGuid();
        Track(parallelAccountId, parallelEmail);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.Users.CountAsync(x => x.NormalizedUserName == account.ToUpperInvariant())).Should().Be(1);
        (await db.Users.CountAsync(x => x.NormalizedEmail == email.ToUpperInvariant())).Should().Be(1);
        (await db.UserPreferences.CountAsync(x => x.AccountId == originalAccountId)).Should().Be(1);
        (await db.Set<ApplicationUserRole>().CountAsync(x => x.UserId == originalAccountId)).Should().Be(1);
        (await db.EmailMessages.CountAsync(x => x.Recipient == email)).Should().Be(1);
    }

    // 測試案例：TC-ERR-AUTH-008（不存在帳號、錯誤密碼、停用帳號與後續正確登入合併驗證）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 無效帳密與停用帳號應使用相同錯誤且不得鎖定有效帳號()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string account = $"login-{suffix}";
        string email = $"login-{suffix}@example.test";
        HttpResponseMessage registered = await RegisterAsync(account, email);
        JsonElement registeredResult = JsonDocument.Parse(await registered.Content.ReadAsStringAsync()).RootElement;
        Guid accountId = registeredResult.GetProperty("accountId").GetGuid();
        Track(accountId, email);

        _factory.ClientAddressProvider.ClientAddress = $"missing-{suffix}";
        HttpResponseMessage missingAccount = await LoginAsync($"missing-{suffix}", ValidPassword);
        _factory.ClientAddressProvider.ClientAddress = $"wrong-password-{suffix}";
        var wrongPasswords = new List<HttpResponseMessage>();
        for (int attempt = 0; attempt < 4; attempt++)
        {
            wrongPasswords.Add(await LoginAsync(account, "Wrong_password123!"));
        }
        (Guid disabledAccountId, string disabledAccountName, _) = await CreateUserAsync(SystemRoles.User, true);
        _factory.ClientAddressProvider.ClientAddress = $"disabled-{suffix}";
        await SetAccountEnabledAsync(disabledAccountId, false);
        HttpResponseMessage disabledAccount = await LoginAsync(disabledAccountName, ValidPassword);

        string missingMessage = await AssertInvalidCredentialsAsync(missingAccount);
        var wrongMessages = new List<string>();
        foreach (HttpResponseMessage wrongPassword in wrongPasswords)
        {
            wrongMessages.Add(await AssertInvalidCredentialsAsync(wrongPassword));
        }
        string disabledMessage = await AssertInvalidCredentialsAsync(disabledAccount);
        wrongMessages.Should().OnlyContain(message => message == missingMessage);
        disabledMessage.Should().Be(missingMessage);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.RefreshTokens.CountAsync(x => x.AccountId == accountId)).Should().Be(0);
        }

        HttpResponseMessage successfulLogin = await LoginAsync(account, ValidPassword);
        successfulLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        successfulLogin.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies).Should().BeTrue();
        cookies.Should().Contain(value => value.Contains("PMW-REFRESH=", StringComparison.Ordinal));

        await using AsyncServiceScope verificationScope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext verificationDb = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ApplicationUser user = await verificationDb.Users.SingleAsync(x => x.Id == accountId);
        user.IsEnabled.Should().BeTrue();
        user.LockoutEnd.Should().BeNull();
        user.AccessFailedCount.Should().Be(0);
        (await verificationDb.RefreshTokens.CountAsync(x => x.AccountId == accountId)).Should().Be(1);
    }

    // 測試案例：TC-SEC-AUTH-019（帳號／IP／跨執行個體限流、可信來源與安全稽核合併驗證）
    // 測試結果：Passed（24 個 Auth API／SQL 回歸測試全數通過）
    // 上次測試時間：2026-09-16 10:34:37 +08:00
    [Test]
    public async Task 登入失敗應在第五次套用共享雙維度限流且不得鎖帳號或洩漏敏感資料()
    {
        (Guid accountId, string account, _) = await CreateUserAsync(SystemRoles.User, true);
        string suffix = Guid.NewGuid().ToString("N");

        var accountResponses = new List<HttpResponseMessage>();
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            _factory.ClientAddressProvider.ClientAddress = $"account-dimension-{suffix}-{attempt}";
            accountResponses.Add(await LoginAsync(account, "Wrong_password123!"));
        }
        accountResponses.Take(4).Should().OnlyContain(response => response.StatusCode == HttpStatusCode.Unauthorized);
        await AssertProblemAsync(accountResponses[4], HttpStatusCode.TooManyRequests, "rate_limited");
        await AssertProblemAsync(await LoginAsync(account, ValidPassword), HttpStatusCode.TooManyRequests, "rate_limited");

        string sharedIp = $"ip-dimension-{suffix}";
        _factory.ClientAddressProvider.ClientAddress = sharedIp;
        var ipResponses = new List<HttpResponseMessage>();
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            ipResponses.Add(await LoginAsync($"missing-ip-{suffix}-{attempt}", ValidPassword));
        }
        ipResponses.Take(4).Should().OnlyContain(response => response.StatusCode == HttpStatusCode.Unauthorized);
        await AssertProblemAsync(ipResponses[4], HttpStatusCode.TooManyRequests, "rate_limited");

        await using var secondFactory = new TestWebApplicationFactory(
            Environment.GetEnvironmentVariable("PMW_TEST_SQL_CONNECTION")!,
            jwtPrivateKeyPem: _testPrivateKeyPem,
            timeProvider: _timeProvider);
        using HttpClient secondClient = secondFactory.CreateClient();
        string sharedAccount = $"shared-{suffix}";
        _loginAccountKeyHashes.Add(HashLoginAccountKey(sharedAccount));
        _factory.ClientAddressProvider.ClientAddress = $"instance-a-{suffix}";
        secondFactory.ClientAddressProvider.ClientAddress = $"instance-b-{suffix}";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            (await LoginAsync(sharedAccount, ValidPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            using HttpRequestMessage request = await CreateCsrfRequestAsync(secondClient, HttpMethod.Post, "/api/v1/auth/login", new
            {
                account = sharedAccount,
                password = ValidPassword
            });
            (await secondClient.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        await AssertProblemAsync(await LoginAsync(sharedAccount, ValidPassword), HttpStatusCode.TooManyRequests, "rate_limited");

        _timeProvider.Advance(TimeSpan.FromMinutes(15));
        _factory.ClientAddressProvider.ClientAddress = $"recovered-{suffix}";
        (await LoginAsync(account, ValidPassword)).StatusCode.Should().Be(HttpStatusCode.OK);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ApplicationUser user = await db.Users.SingleAsync(x => x.Id == accountId);
        user.IsEnabled.Should().BeTrue();
        user.LockoutEnd.Should().BeNull();
        user.AccessFailedCount.Should().Be(0);
        (await db.LoginFailureAttempts.CountAsync()).Should().BeGreaterThanOrEqualTo(15);
        string[] auditLogs = _factory.LogSink.Entries
            .Where(entry => entry.Message.Contains("登入", StringComparison.Ordinal))
            .Select(entry => entry.Message)
            .ToArray();
        auditLogs.Should().NotBeEmpty().And.OnlyContain(log =>
            !log.Contains(account, StringComparison.OrdinalIgnoreCase) &&
            !log.Contains(ValidPassword, StringComparison.Ordinal) &&
            !log.Contains("Wrong_password123!", StringComparison.Ordinal));
    }

    // 測試案例：TC-SEC-AUTH-019（非可信 forwarded header、allowlist 與 fail-fast 設定）
    // 測試結果：Passed（非可信 header、allowlist options 與無效設定 fail-fast）
    // 上次測試時間：2026-09-16 10:34:37 +08:00
    [Test]
    public async Task 反向代理來源必須在Allowlist內且錯誤設定應於啟動時失敗()
    {
        string connectionString = Environment.GetEnvironmentVariable("PMW_TEST_SQL_CONNECTION")!;
        string suffix = Guid.NewGuid().ToString("N");
        await using (var untrustedFactory = new TestWebApplicationFactory(
                         connectionString,
                         jwtPrivateKeyPem: _testPrivateKeyPem,
                         timeProvider: _timeProvider,
                         replaceClientAddressProvider: false))
        {
            using HttpClient untrustedClient = untrustedFactory.CreateClient();
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                string account = $"forwarded-{suffix}-{attempt}";
                _loginAccountKeyHashes.Add(HashLoginAccountKey(account));
                using HttpRequestMessage request = await CreateCsrfRequestAsync(
                    untrustedClient,
                    HttpMethod.Post,
                    "/api/v1/auth/login",
                    new { account, password = ValidPassword });
                request.Headers.Add("X-Forwarded-For", $"203.0.113.{attempt}");
                HttpResponseMessage response = await untrustedClient.SendAsync(request);
                if (attempt < 5)
                {
                    response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                }
                else
                {
                    await AssertProblemAsync(response, HttpStatusCode.TooManyRequests, "rate_limited");
                }
            }
        }

        await using (var trustedFactory = new TestWebApplicationFactory(
                         connectionString,
                         jwtPrivateKeyPem: _testPrivateKeyPem,
                         knownProxy: "127.0.0.1",
                         knownNetwork: "10.0.0.0/8"))
        {
            using HttpClient _ = trustedFactory.CreateClient();
            await using AsyncServiceScope scope = trustedFactory.Services.CreateAsyncScope();
            ForwardedHeadersOptions options = scope.ServiceProvider
                .GetRequiredService<IOptions<ForwardedHeadersOptions>>()
                .Value;
            options.ForwardLimit.Should().Be(1);
            options.KnownProxies.Should().Contain(IPAddress.Parse("127.0.0.1"));
            options.KnownIPNetworks.Should().Contain(network => network.Contains(IPAddress.Parse("10.12.34.56")));
        }

        await using var invalidFactory = new TestWebApplicationFactory(
            connectionString,
            jwtPrivateKeyPem: _testPrivateKeyPem,
            knownProxy: "not-an-ip");
        Action startInvalidConfiguration = () => invalidFactory.CreateClient();
        startInvalidConfiguration.Should().Throw<InvalidOperationException>()
            .WithMessage("*ReverseProxy:KnownProxies*not-an-ip*");
    }

    // 測試案例：TC-F-AUTH-007（未驗證帳號即使 DB 角色較高，登入後仍只能取得 Viewer 能力）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 未驗證Administrator登入後應降為Viewer且禁止寫入()
    {
        (Guid accountId, string account, string email) = await CreateUserAsync(SystemRoles.Administrator, false);

        HttpResponseMessage login = await LoginAsync(account, ValidPassword);
        JsonElement loginResult = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement;
        string accessToken = loginResult.GetProperty("accessToken").GetString()!;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        JsonElement current = await _client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        current.GetProperty("id").GetGuid().Should().Be(accountId);
        current.GetProperty("email").GetString().Should().Be(email);
        current.GetProperty("emailConfirmed").GetBoolean().Should().BeFalse();
        current.GetProperty("role").GetString().Should().Be(SystemRoles.Viewer);
        current.GetProperty("functions").EnumerateArray().Select(x => x.GetString()).Should()
            .BeEquivalentTo(GetExpectedFunctions(SystemRoles.Viewer));

        HttpResponseMessage readable = await _client.GetAsync("/api/v1/projects");
        HttpResponseMessage forbiddenWrite = await _client.PostAsJsonAsync("/api/v1/projects", new
        {
            name = "Viewer 不得建立",
            ownerAccountId = accountId,
            timeZoneId = "Asia/Taipei"
        });
        string forbiddenBody = await forbiddenWrite.Content.ReadAsStringAsync();
        JsonElement forbidden = JsonDocument.Parse(forbiddenBody).RootElement;

        readable.StatusCode.Should().Be(HttpStatusCode.OK);
        forbiddenWrite.StatusCode.Should().Be(HttpStatusCode.Forbidden, forbiddenBody);
        forbidden.GetProperty("code").GetString().Should().Be("forbidden");
    }

    // 測試案例：TC-F-AUTH-020（下列三個角色共用同一資料驅動測試）
    // 測試結果：Passed（3 tests：User、Administrator、Admin）
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [TestCase(SystemRoles.User)]
    [TestCase(SystemRoles.Administrator)]
    [TestCase(SystemRoles.Admin)]
    public async Task 已驗證帳號登入應只回AccessToken並由Me回傳正確角色能力(string role)
    {
        (Guid accountId, string account, string email) = await CreateUserAsync(role, true);

        HttpResponseMessage login = await LoginAsync(account, ValidPassword);
        string loginBody = await login.Content.ReadAsStringAsync();
        JsonElement loginResult = JsonDocument.Parse(loginBody).RootElement;
        string accessToken = loginResult.GetProperty("accessToken").GetString()!;

        login.StatusCode.Should().Be(HttpStatusCode.OK, loginBody);
        loginResult.TryGetProperty("refreshToken", out _).Should().BeFalse();
        loginResult.TryGetProperty("accessTokenExpiresAt", out _).Should().BeTrue();
        string refreshCookie = ExtractCookie(login, "PMW-REFRESH");
        login.Headers.GetValues("Set-Cookie").Should().Contain(value =>
            value.Contains("httponly", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("path=/api/v1/auth", StringComparison.OrdinalIgnoreCase));

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        JsonElement current = await _client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        current.GetProperty("id").GetGuid().Should().Be(accountId);
        current.GetProperty("account").GetString().Should().Be(account);
        current.GetProperty("email").GetString().Should().Be(email);
        current.GetProperty("emailConfirmed").GetBoolean().Should().BeTrue();
        current.GetProperty("isEnabled").GetBoolean().Should().BeTrue();
        current.GetProperty("role").GetString().Should().Be(role);
        current.GetProperty("functions").EnumerateArray().Select(x => x.GetString()).Should()
            .BeEquivalentTo(GetExpectedFunctions(role));

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.RefreshTokens.SingleAsync(x => x.AccountId == accountId);
        stored.TokenHash.Should().HaveLength(64).And.Be(HashRefreshToken(refreshCookie));
        stored.TokenHash.Should().NotBe(refreshCookie);
        stored.RevokedAt.Should().BeNull();
    }

    // 測試案例：TC-SEC-API-003（JWT claims、15 分鐘期限、驗簽與 TokenVersion 即時失效）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Jwt應驗證Claims期限IssuerAudienceSignature與帳號TokenVersion()
    {
        (Guid accountId, string account, _) = await CreateUserAsync(SystemRoles.User, true);
        HttpResponseMessage login = await LoginAsync(account, ValidPassword);
        string body = await login.Content.ReadAsStringAsync();
        string accessToken = JsonDocument.Parse(body).RootElement.GetProperty("accessToken").GetString()!;
        var handler = new JwtSecurityTokenHandler();
        JwtSecurityToken parsed = handler.ReadJwtToken(accessToken);

        parsed.Subject.Should().Be(accountId.ToString());
        parsed.Id.Should().NotBeNullOrWhiteSpace();
        parsed.Issuer.Should().Be("ProjectManagementWeb");
        parsed.Audiences.Should().Equal("ProjectManagementWeb.Spa");
        parsed.Claims.Single(claim => claim.Type == "role").Value.Should().Be(SystemRoles.User);
        parsed.Claims.Where(claim => claim.Type == "permission").Select(claim => claim.Value)
            .Should().BeEquivalentTo(GetExpectedFunctions(SystemRoles.User));
        parsed.Claims.Single(claim => claim.Type == "token_version").Value.Should().Be("0");
        (parsed.ValidTo - parsed.ValidFrom).Should().Be(TimeSpan.FromMinutes(15));

        using RSA signingKey = RSA.Create();
        signingKey.ImportFromPem(_testPrivateKeyPem);
        using RSA otherKey = RSA.Create(2048);
        DateTime utcNow = DateTime.UtcNow;
        string[] invalidTokens =
        [
            CreateAccessToken(signingKey, accountId, "wrong-issuer", "ProjectManagementWeb.Spa", utcNow.AddMinutes(5)),
            CreateAccessToken(signingKey, accountId, "ProjectManagementWeb", "wrong-audience", utcNow.AddMinutes(5)),
            CreateAccessToken(signingKey, accountId, "ProjectManagementWeb", "ProjectManagementWeb.Spa", utcNow.AddMinutes(-2)),
            CreateAccessToken(otherKey, accountId, "ProjectManagementWeb", "ProjectManagementWeb.Spa", utcNow.AddMinutes(5))
        ];
        foreach (string invalidToken in invalidTokens)
        {
            using var invalidClient = _factory.CreateClient();
            invalidClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", invalidToken);
            (await invalidClient.GetAsync("/api/v1/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ApplicationUser user = await db.Users.SingleAsync(x => x.Id == accountId);
            user.TokenVersion++;
            await db.SaveChangesAsync();
        }
        using var revokedClient = _factory.CreateClient();
        revokedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        (await revokedClient.GetAsync("/api/v1/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // 測試案例：TC-F-AUTH-012（rotation、舊 token reuse 與新 token family 撤銷合併驗證）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Refresh輪替後重用舊Token應撤銷整個Family()
    {
        (Guid accountId, string account, _) = await CreateUserAsync(SystemRoles.User, true);
        HttpResponseMessage login = await LoginAsync(account, ValidPassword);
        string oldToken = ExtractCookie(login, "PMW-REFRESH");

        using var firstRefreshRequest = await CreateCsrfRequestAsync(HttpMethod.Post, "/api/v1/auth/refresh", null);
        HttpResponseMessage firstRefresh = await _client.SendAsync(firstRefreshRequest);
        string firstRefreshBody = await firstRefresh.Content.ReadAsStringAsync();
        firstRefresh.StatusCode.Should().Be(HttpStatusCode.OK, firstRefreshBody);
        string newToken = ExtractCookie(firstRefresh, "PMW-REFRESH");
        newToken.Should().NotBe(oldToken);

        using HttpClient oldTokenClient = _factory.CreateClient(new() { HandleCookies = false });
        HttpResponseMessage oldTokenReuse = await SendWithRefreshCookieAsync(oldTokenClient, "/api/v1/auth/refresh", oldToken);
        await AssertProblemAsync(oldTokenReuse, HttpStatusCode.Unauthorized, "refresh_token_reuse");

        using HttpClient newTokenClient = _factory.CreateClient(new() { HandleCookies = false });
        HttpResponseMessage revokedNewToken = await SendWithRefreshCookieAsync(newTokenClient, "/api/v1/auth/refresh", newToken);
        await AssertProblemAsync(revokedNewToken, HttpStatusCode.Unauthorized, "refresh_token_reuse");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var family = await db.RefreshTokens.Where(x => x.AccountId == accountId).ToArrayAsync();
        family.Should().HaveCount(2);
        family.Select(x => x.FamilyId).Distinct().Should().ContainSingle();
        family.Should().OnlyContain(x => x.RevokedAt != null);
        var oldTokenEntity = family.Single(x => x.TokenHash == HashRefreshToken(oldToken));
        var newTokenEntity = family.Single(x => x.TokenHash == HashRefreshToken(newToken));
        oldTokenEntity.ReplacedByTokenId.Should().Be(newTokenEntity.Id);
        newTokenEntity.ReplacedByTokenId.Should().BeNull();
    }

    // 測試案例：TC-ST-AUTH-013（有效、缺少與未知 cookie 的 logout 合併驗證；API、SQL）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Logout應撤銷有效Token並對缺少或未知Cookie保持冪等()
    {
        (Guid accountId, string account, _) = await CreateUserAsync(SystemRoles.User, true);
        HttpResponseMessage login = await LoginAsync(account, ValidPassword);
        string validToken = ExtractCookie(login, "PMW-REFRESH");

        using var validLogoutRequest = await CreateCsrfRequestAsync(HttpMethod.Post, "/api/v1/auth/logout", null);
        HttpResponseMessage validLogout = await _client.SendAsync(validLogoutRequest);
        validLogout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        validLogout.Headers.GetValues("Set-Cookie").Should().Contain(value =>
            value.Contains("PMW-REFRESH=", StringComparison.Ordinal) &&
            value.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        using var refreshAfterLogout = await CreateCsrfRequestAsync(HttpMethod.Post, "/api/v1/auth/refresh", null);
        HttpResponseMessage refreshAfterLogoutResponse = await _client.SendAsync(refreshAfterLogout);
        await AssertProblemAsync(refreshAfterLogoutResponse, HttpStatusCode.Unauthorized, "missing_refresh_token");

        using HttpClient missingClient = _factory.CreateClient();
        using var missingLogoutRequest = await CreateCsrfRequestAsync(missingClient, HttpMethod.Post, "/api/v1/auth/logout", null);
        HttpResponseMessage missingLogout = await missingClient.SendAsync(missingLogoutRequest);
        missingLogout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertRefreshCookieCleared(missingLogout);

        using HttpClient unknownClient = _factory.CreateClient(new() { HandleCookies = false });
        HttpResponseMessage unknownLogout = await SendWithRefreshCookieAsync(
            unknownClient, "/api/v1/auth/logout", Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
        unknownLogout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertRefreshCookieCleared(unknownLogout);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var token = await db.RefreshTokens.SingleAsync(x => x.AccountId == accountId);
        token.TokenHash.Should().Be(HashRefreshToken(validToken));
        token.RevokedAt.Should().NotBeNull();
        (await db.RefreshTokens.CountAsync(x => x.AccountId == accountId)).Should().Be(1);
    }

    // 測試案例：TC-ST-AUTH-009（Email 驗證成功後維持 Viewer）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Email驗證成功後應維持Viewer且不自動提升角色()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string account = $"confirm-{suffix}";
        string email = $"confirm-{suffix}@example.test";
        HttpResponseMessage registered = await RegisterAsync(account, email);
        JsonElement registeredResult = JsonDocument.Parse(await registered.Content.ReadAsStringAsync()).RootElement;
        Guid accountId = registeredResult.GetProperty("accountId").GetGuid();
        Track(accountId, email);
        string body = _factory.EmailGateway.Bodies.Should().ContainSingle().Which;
        string url = body[(body.IndexOf("href=\"", StringComparison.Ordinal) + 6)..body.IndexOf("\">驗證帳號", StringComparison.Ordinal)];
        var query = QueryHelpers.ParseQuery(new Uri(url).Query);

        using var confirmRequest = await CreateCsrfRequestAsync(HttpMethod.Post, "/api/v1/auth/email/confirm", new
        {
            accountId,
            token = query["token"].Single()
        });
        HttpResponseMessage confirm = await _client.SendAsync(confirmRequest);
        string confirmBody = await confirm.Content.ReadAsStringAsync();

        confirm.StatusCode.Should().Be(HttpStatusCode.OK, confirmBody);
        JsonDocument.Parse(confirmBody).RootElement.GetBoolean().Should().BeTrue();

        HttpResponseMessage login = await LoginAsync(account, ValidPassword);
        JsonElement loginResult = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", loginResult.GetProperty("accessToken").GetString());
        JsonElement current = await _client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        current.GetProperty("emailConfirmed").GetBoolean().Should().BeTrue();
        current.GetProperty("role").GetString().Should().Be(SystemRoles.Viewer);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = await db.Users.SingleAsync(x => x.Id == accountId);
        user.EmailConfirmed.Should().BeTrue();
        (await userManager.GetRolesAsync(user)).Should().Equal(SystemRoles.Viewer);
    }

    // 測試案例：TC-ERR-AUTH-010（無效、變造與精確三分鐘邊界合併驗證）
    // 測試結果：Passed（Unit／API／SQL；2:59.999 有效，3:00.000 起及無效／變造值拒絕）
    // 上次測試時間：2026-09-15 22:44:47 +08:00
    [Test]
    public async Task Email驗證Token應在三分鐘前有效且自滿三分鐘起拒絕無效與變造值()
    {
        (Guid beforeId, string beforeToken) = await RegisterAndReadVerificationTokenAsync("before-expiry");
        (Guid atId, string atToken) = await RegisterAndReadVerificationTokenAsync("at-expiry");
        (Guid afterId, string afterToken) = await RegisterAndReadVerificationTokenAsync("after-expiry");
        (Guid tamperedId, string tamperedToken) = await RegisterAndReadVerificationTokenAsync("tampered");

        _timeProvider.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromMilliseconds(1));
        HttpResponseMessage beforeExpiry = await ConfirmEmailAsync(beforeId, beforeToken);
        beforeExpiry.StatusCode.Should().Be(HttpStatusCode.OK, await beforeExpiry.Content.ReadAsStringAsync());

        _timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await AssertProblemAsync(await ConfirmEmailAsync(atId, atToken), HttpStatusCode.BadRequest, "invalid_email_token");

        _timeProvider.Advance(TimeSpan.FromTicks(1));
        await AssertProblemAsync(await ConfirmEmailAsync(afterId, afterToken), HttpStatusCode.BadRequest, "invalid_email_token");
        await AssertProblemAsync(
            await ConfirmEmailAsync(tamperedId, tamperedToken[..^1] + (tamperedToken[^1] == 'A' ? "B" : "A")),
            HttpStatusCode.BadRequest,
            "invalid_email_token");
        await AssertProblemAsync(
            await ConfirmEmailAsync(tamperedId, "%%%不是有效的Token%%%"),
            HttpStatusCode.BadRequest,
            "invalid_email_token");
    }

    // 測試案例：TC-ERR-AUTH-015、TC-ERR-AUTH-022（成功重寄使舊 Token 失效；寄送失敗只留安全 Log 且保留舊 Token）
    // 測試結果：Passed（成功重寄只保留 Token B；SMTP 失敗只記安全 Log 且 Token A 保持有效）
    // 上次測試時間：2026-09-15 22:44:47 +08:00
    [Test]
    public async Task 重寄成功應只保留最新版Token而寄送失敗不得使原Token失效()
    {
        (Guid successId, string successAccount, string successTokenA) =
            await RegisterAndReadVerificationTokenWithAccountAsync("resend-success");
        _factory.EmailGateway.Reset();
        (await SendResendAsync(successAccount)).StatusCode.Should().Be(HttpStatusCode.OK);
        string successTokenB = ReadVerificationToken(_factory.EmailGateway.Bodies.Should().ContainSingle().Which);

        await AssertProblemAsync(
            await ConfirmEmailAsync(successId, successTokenA),
            HttpStatusCode.BadRequest,
            "invalid_email_token");
        (await ConfirmEmailAsync(successId, successTokenB)).StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.EmailGateway.Reset();
        (Guid failedId, string failedAccount, string failedTokenA) =
            await RegisterAndReadVerificationTokenWithAccountAsync("resend-failed");
        _factory.EmailGateway.Reset();
        _factory.EmailGateway.ShouldFail = true;
        (await SendResendAsync(failedAccount)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ConfirmEmailAsync(failedId, failedTokenA)).StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LogSink.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("驗證信寄送失敗", StringComparison.Ordinal) &&
            !entry.Message.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    // 測試案例：TC-ERR-AUTH-016（已使用 Token 重放）
    // 測試結果：Passed（首次成功，重放固定回 400 invalid_email_token）
    // 上次測試時間：2026-09-15 22:44:47 +08:00
    [Test]
    public async Task Email驗證Token成功使用後再次使用應回固定無效錯誤()
    {
        (Guid accountId, string token) = await RegisterAndReadVerificationTokenAsync("replay");

        (await ConfirmEmailAsync(accountId, token)).StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertProblemAsync(
            await ConfirmEmailAsync(accountId, token),
            HttpStatusCode.BadRequest,
            "invalid_email_token");
    }

    // 測試案例：TC-ERR-AUTH-017（跨帳號攻擊不消耗合法 Token）
    // 測試結果：Passed（跨帳號失敗不消耗 Token，原帳號後續成功）
    // 上次測試時間：2026-09-15 22:44:47 +08:00
    [Test]
    public async Task Email驗證Token跨帳號使用應失敗且原帳號仍可合法使用()
    {
        (Guid accountAId, string tokenA) = await RegisterAndReadVerificationTokenAsync("cross-account-a");
        (Guid accountBId, _) = await RegisterAndReadVerificationTokenAsync("cross-account-b");

        await AssertProblemAsync(
            await ConfirmEmailAsync(accountBId, tokenA),
            HttpStatusCode.BadRequest,
            "invalid_email_token");
        (await ConfirmEmailAsync(accountAId, tokenA)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 測試案例：TC-ERR-AUTH-018（冷卻、帳號／IP 滾動上限、不存在帳號及視窗恢復合併驗證）
    // 測試結果：Passed（59.999／60 秒、帳號與 IP 第 5／6 次、不存在帳號、視窗恢復）
    // 上次測試時間：2026-09-15 22:44:47 +08:00
    [Test]
    public async Task 重寄驗證信應套用精確冷卻與帳號Ip滾動上限且受限請求不使Token失效()
    {
        (Guid limitedAccountId, string limitedAccount, _) = await CreateUserAsync(SystemRoles.Viewer, false);
        _factory.EmailGateway.Reset();

        _factory.ClientAddressProvider.ClientAddress = NewClientAddress();
        (await SendResendAsync(limitedAccount)).StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.EmailGateway.Bodies.Should().HaveCount(1);

        _timeProvider.Advance(TimeSpan.FromSeconds(60) - TimeSpan.FromMilliseconds(1));
        _factory.ClientAddressProvider.ClientAddress = NewClientAddress();
        (await SendResendAsync(limitedAccount)).StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.EmailGateway.Bodies.Should().HaveCount(1, "59.999 秒仍在冷卻期內");

        _timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        _factory.ClientAddressProvider.ClientAddress = NewClientAddress();
        (await SendResendAsync(limitedAccount)).StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.EmailGateway.Bodies.Should().HaveCount(2, "滿 60 秒應可再次寄送");

        for (int allowedNumber = 3; allowedNumber <= 5; allowedNumber++)
        {
            _timeProvider.Advance(TimeSpan.FromSeconds(60));
            _factory.ClientAddressProvider.ClientAddress = NewClientAddress();
            (await SendResendAsync(limitedAccount)).StatusCode.Should().Be(HttpStatusCode.OK);
            _factory.EmailGateway.Bodies.Should().HaveCount(allowedNumber);
        }

        string latestAllowedToken = ReadVerificationToken(_factory.EmailGateway.Bodies.Last());
        _timeProvider.Advance(TimeSpan.FromSeconds(60));
        _factory.ClientAddressProvider.ClientAddress = NewClientAddress();
        HttpResponseMessage accountSixth = await SendResendAsync(limitedAccount);
        accountSixth.StatusCode.Should().Be(HttpStatusCode.OK, await accountSixth.Content.ReadAsStringAsync());
        _factory.EmailGateway.Bodies.Should().HaveCount(5, "同帳號第六次只回一般訊息而不寄信");
        (await ConfirmEmailAsync(limitedAccountId, latestAllowedToken)).StatusCode.Should().Be(
            HttpStatusCode.OK,
            "帳號受限請求不得使現有 Token 失效");

        (_, string recoveryAccount, _) = await CreateUserAsync(SystemRoles.Viewer, false);
        _factory.EmailGateway.Reset();
        for (int allowedNumber = 1; allowedNumber <= 5; allowedNumber++)
        {
            if (allowedNumber > 1)
            {
                _timeProvider.Advance(TimeSpan.FromSeconds(60));
            }
            _factory.ClientAddressProvider.ClientAddress = NewClientAddress();
            (await SendResendAsync(recoveryAccount)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        _factory.EmailGateway.Bodies.Should().HaveCount(5);

        _timeProvider.Advance(TimeSpan.FromSeconds(60));
        _factory.ClientAddressProvider.ClientAddress = NewClientAddress();
        (await SendResendAsync(recoveryAccount)).StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.EmailGateway.Bodies.Should().HaveCount(5);

        _timeProvider.Advance(TimeSpan.FromMinutes(55));
        _factory.ClientAddressProvider.ClientAddress = NewClientAddress();
        (await SendResendAsync(recoveryAccount)).StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.EmailGateway.Bodies.Should().HaveCount(6, "最早允許請求離開滾動 60 分鐘視窗後應恢復寄送");

        _factory.EmailGateway.Reset();
        string sharedClientAddress = NewClientAddress();
        for (int requestNumber = 1; requestNumber <= 5; requestNumber++)
        {
            _factory.ClientAddressProvider.ClientAddress = sharedClientAddress;
            HttpResponseMessage missing = await SendResendAsync($"missing-{Guid.NewGuid():N}");
            missing.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        HttpResponseMessage ipSixth = await SendResendAsync($"missing-{Guid.NewGuid():N}");
        await AssertProblemAsync(ipSixth, HttpStatusCode.TooManyRequests, "rate_limited");
        _factory.EmailGateway.Bodies.Should().BeEmpty("不存在帳號只累計 IP 且不得寄信");

        _timeProvider.Advance(TimeSpan.FromMinutes(60));
        (_, string ipRecoveryAccount, _) = await CreateUserAsync(SystemRoles.Viewer, false);
        _factory.ClientAddressProvider.ClientAddress = sharedClientAddress;
        HttpResponseMessage ipRecovered = await SendResendAsync(ipRecoveryAccount);
        ipRecovered.StatusCode.Should().Be(HttpStatusCode.OK, await ipRecovered.Content.ReadAsStringAsync());
        _factory.EmailGateway.Bodies.Should().ContainSingle("IP 滾動視窗結束後應恢復寄送");
    }

    // 測試案例：TC-F-AUTH-011（未驗證啟用、已驗證、停用與不存在帳號合併驗證）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 重寄驗證信應一律回成功且只寄給未驗證啟用帳號()
    {
        (_, string eligibleAccount, string eligibleEmail) = await CreateUserAsync(SystemRoles.Viewer, false);
        (_, string confirmedAccount, _) = await CreateUserAsync(SystemRoles.Viewer, true);
        (Guid disabledId, string disabledAccount, _) = await CreateUserAsync(SystemRoles.Viewer, false);
        await SetAccountEnabledAsync(disabledId, false);

        string[] responses =
        [
            await ResendAsync(eligibleAccount),
            await ResendAsync(confirmedAccount),
            await ResendAsync(disabledAccount),
            await ResendAsync($"missing-{Guid.NewGuid():N}")
        ];

        responses.Should().OnlyContain(x => x == "true");
        _factory.EmailGateway.Recipients.Should().Equal(eligibleEmail);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.EmailMessages.CountAsync(x => x.Recipient == eligibleEmail)).Should().Be(1);
        (await db.EmailMessages.CountAsync(x => x.Recipient != eligibleEmail &&
            _emails.Contains(x.Recipient))).Should().Be(0);
    }

    // 測試案例：TC-ERR-AUTH-022（重寄時 Email gateway 失敗）
    // 測試結果：Passed（API／SQL／安全 Warning Log；恢復及限制另由 TC-ERR-AUTH-015、018 合併驗證）
    // 上次測試時間：2026-09-15 22:44:47 +08:00
    [Test]
    public async Task 重寄驗證信遇EmailGateway失敗仍應回一般成功並記錄Failed()
    {
        (_, string account, string accountEmail) = await CreateUserAsync(SystemRoles.Viewer, false);
        (_, _, string emailInput) = await CreateUserAsync(SystemRoles.Viewer, false);
        _factory.EmailGateway.ShouldFail = true;

        string accountResponseBody = await ResendAsync(account);
        string emailResponseBody = await ResendAsync(emailInput);

        accountResponseBody.Should().Be("true");
        emailResponseBody.Should().Be("true");
        _factory.EmailGateway.Recipients.Should().Equal(accountEmail, emailInput);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var messages = await db.EmailMessages
            .Where(x => x.Recipient == accountEmail || x.Recipient == emailInput)
            .ToArrayAsync();
        messages.Should().HaveCount(2).And.OnlyContain(message =>
            message.Status == EmailDeliveryStatus.Failed &&
            message.AttemptCount == 1 &&
            message.LastError == "測試用 Email gateway 失敗。" &&
            !message.LastError.Contains("token", StringComparison.OrdinalIgnoreCase));
        string[] failureLogs = _factory.LogSink.Entries
            .Where(entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("驗證信寄送失敗", StringComparison.Ordinal))
            .Select(entry => entry.Message)
            .ToArray();
        failureLogs.Should().HaveCount(2).And.OnlyContain(log =>
            !log.Contains(accountEmail, StringComparison.OrdinalIgnoreCase) &&
            !log.Contains(emailInput, StringComparison.OrdinalIgnoreCase) &&
            !log.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    // 測試案例：TC-ERR-AUTH-021（缺少、未知、過期、已撤銷與停用帳號 token 合併驗證）
    // 測試結果：Passed（缺少、未知、過期、已撤銷與停用帳號五種分支皆符合規格）
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Refresh失敗矩陣應回固定錯誤並撤銷必要TokenFamily()
    {
        using HttpClient missingClient = _factory.CreateClient();
        using var missingRequest = await CreateCsrfRequestAsync(missingClient, HttpMethod.Post, "/api/v1/auth/refresh", null);
        await AssertProblemAsync(
            await missingClient.SendAsync(missingRequest), HttpStatusCode.Unauthorized, "missing_refresh_token");

        using HttpClient unknownClient = _factory.CreateClient(new() { HandleCookies = false });
        await AssertProblemAsync(
            await SendWithRefreshCookieAsync(unknownClient, "/api/v1/auth/refresh", CreateRawRefreshToken()),
            HttpStatusCode.Unauthorized,
            "invalid_refresh_token");

        (Guid expiredAccountId, _, _) = await CreateUserAsync(SystemRoles.User, true);
        Guid expiredFamilyId = Guid.NewGuid();
        string expiredRaw = CreateRawRefreshToken();
        await SeedRefreshTokenAsync(expiredAccountId, expiredFamilyId, expiredRaw, DateTimeOffset.UtcNow.AddMinutes(-1));
        await SeedRefreshTokenAsync(
            expiredAccountId, expiredFamilyId, CreateRawRefreshToken(), DateTimeOffset.UtcNow.AddHours(1));
        using HttpClient expiredClient = _factory.CreateClient(new() { HandleCookies = false });
        await AssertProblemAsync(
            await SendWithRefreshCookieAsync(expiredClient, "/api/v1/auth/refresh", expiredRaw),
            HttpStatusCode.Unauthorized,
            "refresh_token_reuse");
        await AssertFamilyRevokedAsync(expiredAccountId, expiredFamilyId, 2);

        (Guid revokedAccountId, _, _) = await CreateUserAsync(SystemRoles.User, true);
        Guid revokedFamilyId = Guid.NewGuid();
        string revokedRaw = CreateRawRefreshToken();
        await SeedRefreshTokenAsync(
            revokedAccountId, revokedFamilyId, revokedRaw, DateTimeOffset.UtcNow.AddHours(1), true);
        await SeedRefreshTokenAsync(
            revokedAccountId, revokedFamilyId, CreateRawRefreshToken(), DateTimeOffset.UtcNow.AddHours(1));
        using HttpClient revokedClient = _factory.CreateClient(new() { HandleCookies = false });
        await AssertProblemAsync(
            await SendWithRefreshCookieAsync(revokedClient, "/api/v1/auth/refresh", revokedRaw),
            HttpStatusCode.Unauthorized,
            "refresh_token_reuse");
        await AssertFamilyRevokedAsync(revokedAccountId, revokedFamilyId, 2);

        (Guid disabledAccountId, _, _) = await CreateUserAsync(SystemRoles.User, true);
        Guid disabledFamilyId = Guid.NewGuid();
        string disabledRaw = CreateRawRefreshToken();
        await SeedRefreshTokenAsync(
            disabledAccountId, disabledFamilyId, disabledRaw, DateTimeOffset.UtcNow.AddHours(1));
        await SetAccountEnabledAsync(disabledAccountId, false);
        using HttpClient disabledClient = _factory.CreateClient(new() { HandleCookies = false });
        await AssertProblemAsync(
            await SendWithRefreshCookieAsync(disabledClient, "/api/v1/auth/refresh", disabledRaw),
            HttpStatusCode.Unauthorized,
            "account_disabled");
        await AssertFamilyRevokedAsync(disabledAccountId, disabledFamilyId, 1);
    }

    // 測試案例：TC-ERR-AUTH-005（六個 Auth endpoint 的缺少 header、僅 header、錯誤 token 合併驗證）
    // 測試結果：Passed（18 條 CSRF 拒絕路徑均為 400，且 Auth 資料筆數不變）
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Auth寫入端點遇無效Csrf配對應全部拒絕且不異動資料()
    {
        (int Users, int Preferences, int Tokens, int Emails) before = await GetAuthDataCountsAsync();
        (string Path, object? Body)[] endpoints =
        [
            ("/api/v1/auth/register", new
            {
                account = $"csrf-{Guid.NewGuid():N}",
                password = ValidPassword,
                confirmPassword = ValidPassword,
                email = $"csrf-{Guid.NewGuid():N}@example.test",
                name = "CSRF 測試帳號"
            }),
            ("/api/v1/auth/login", new { account = "csrf-test", password = ValidPassword }),
            ("/api/v1/auth/refresh", null),
            ("/api/v1/auth/logout", null),
            ("/api/v1/auth/email/confirm", new { accountId = Guid.NewGuid(), token = "csrf-test" }),
            ("/api/v1/auth/email/resend", new { accountOrEmail = "csrf-test@example.test" })
        ];

        foreach ((string path, object? body) in endpoints)
        {
            await AssertCsrfRejectedAsync(path, body, CsrfFailureMode.MissingHeader);
            await AssertCsrfRejectedAsync(path, body, CsrfFailureMode.HeaderOnly);
            await AssertCsrfRejectedAsync(path, body, CsrfFailureMode.InvalidToken);
        }

        (int Users, int Preferences, int Tokens, int Emails) after = await GetAuthDataCountsAsync();
        after.Should().Be(before, "CSRF 驗證失敗不得建立或異動 Auth 資料");
    }

    private async Task<HttpResponseMessage> RegisterAsync(string account, string email)
    {
        using var request = await CreateCsrfRequestAsync(HttpMethod.Post, "/api/v1/auth/register", new
        {
            account,
            password = ValidPassword,
            confirmPassword = ValidPassword,
            email,
            name = $"測試使用者 {account}"
        });
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendRegistrationAsync(
        string account,
        string password,
        string confirmPassword,
        string email,
        string name)
    {
        using var request = await CreateCsrfRequestAsync(HttpMethod.Post, "/api/v1/auth/register", new
        {
            account,
            password,
            confirmPassword,
            email,
            name
        });
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> LoginAsync(string account, string password)
    {
        _loginAccountKeyHashes.Add(HashLoginAccountKey(account));
        using var request = await CreateCsrfRequestAsync(HttpMethod.Post, "/api/v1/auth/login", new
        {
            account,
            password
        });
        return await _client.SendAsync(request);
    }

    private async Task<string> ResendAsync(string accountOrEmail)
    {
        using var request = await CreateCsrfRequestAsync(HttpMethod.Post, "/api/v1/auth/email/resend", new
        {
            accountOrEmail
        });
        HttpResponseMessage response = await _client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }

    private Task<HttpRequestMessage> CreateCsrfRequestAsync(HttpMethod method, string path, object? body) =>
        CreateCsrfRequestAsync(_client, method, path, body);

    private static async Task<HttpRequestMessage> CreateCsrfRequestAsync(
        HttpClient client, HttpMethod method, string path, object? body)
    {
        JsonElement csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/security/csrf-token");
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task AssertCsrfRejectedAsync(string path, object? body, CsrfFailureMode mode)
    {
        using HttpClient client = _factory.CreateClient(new() { HandleCookies = mode != CsrfFailureMode.HeaderOnly });
        HttpResponseMessage csrfResponse = await client.GetAsync("/api/v1/security/csrf-token");
        JsonElement csrf = JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync()).RootElement;
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        if (mode != CsrfFailureMode.MissingHeader)
        {
            request.Headers.Add(
                "X-CSRF-TOKEN",
                mode == CsrfFailureMode.HeaderOnly
                    ? csrf.GetProperty("token").GetString()
                    : "invalid-csrf-token");
        }

        HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "{0} 在 {1} 情境必須先被 antiforgery filter 拒絕",
            path,
            mode);
    }

    private async Task<(int Users, int Preferences, int Tokens, int Emails)> GetAuthDataCountsAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (
            await db.Users.CountAsync(),
            await db.UserPreferences.CountAsync(),
            await db.RefreshTokens.CountAsync(),
            await db.EmailMessages.CountAsync());
    }

    private async Task<(Guid AccountId, string Token)> RegisterAndReadVerificationTokenAsync(string prefix)
    {
        (Guid accountId, _, string token) = await RegisterAndReadVerificationTokenWithAccountAsync(prefix);
        return (accountId, token);
    }

    private async Task<(Guid AccountId, string Account, string Token)> RegisterAndReadVerificationTokenWithAccountAsync(
        string prefix)
    {
        _factory.EmailGateway.Reset();
        string suffix = Guid.NewGuid().ToString("N");
        string account = $"{prefix}-{suffix}";
        string email = $"{prefix}-{suffix}@example.test";
        HttpResponseMessage response = await RegisterAsync(account, email);
        string responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, responseBody);
        Guid accountId = JsonDocument.Parse(responseBody).RootElement.GetProperty("accountId").GetGuid();
        Track(accountId, email);
        string token = ReadVerificationToken(_factory.EmailGateway.Bodies.Should().ContainSingle().Which);
        return (accountId, account, token);
    }

    private static string ReadVerificationToken(string emailBody)
    {
        int urlStart = emailBody.IndexOf("href=\"", StringComparison.Ordinal) + 6;
        int urlEnd = emailBody.IndexOf("\">驗證帳號", urlStart, StringComparison.Ordinal);
        string url = emailBody[urlStart..urlEnd];
        return QueryHelpers.ParseQuery(new Uri(url).Query)["token"].Single()!;
    }

    private async Task<HttpResponseMessage> ConfirmEmailAsync(Guid accountId, string token)
    {
        using var request = await CreateCsrfRequestAsync(HttpMethod.Post, "/api/v1/auth/email/confirm", new
        {
            accountId,
            token
        });
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendResendAsync(string accountOrEmail)
    {
        using var request = await CreateCsrfRequestAsync(HttpMethod.Post, "/api/v1/auth/email/resend", new
        {
            accountOrEmail
        });
        return await _client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendWithRefreshCookieAsync(
        HttpClient client, string path, string refreshToken)
    {
        HttpResponseMessage csrfResponse = await client.GetAsync("/api/v1/security/csrf-token");
        JsonElement csrf = JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync()).RootElement;
        string csrfCookie = ExtractCookie(csrfResponse, "PMW-CSRF");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Headers.Add("Cookie", $"PMW-CSRF={csrfCookie}; PMW-REFRESH={refreshToken}");
        return await client.SendAsync(request);
    }

    private static async Task AssertRegistrationFieldErrorAsync(HttpResponseMessage response, string field)
    {
        string body = await response.Content.ReadAsStringAsync();
        JsonElement problem = JsonDocument.Parse(body).RootElement;
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        problem.GetProperty("code").GetString().Should().Be("registration_failed");
        problem.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
    }

    private static async Task AssertProblemWithFieldErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string code,
        string field)
    {
        string body = await response.Content.ReadAsStringAsync();
        JsonElement problem = JsonDocument.Parse(body).RootElement;
        response.StatusCode.Should().Be(statusCode, body);
        problem.GetProperty("code").GetString().Should().Be(code);
        problem.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
    }

    private static async Task<string> AssertInvalidCredentialsAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        JsonElement problem = JsonDocument.Parse(body).RootElement;
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        problem.GetProperty("code").GetString().Should().Be("invalid_credentials");
        problem.GetProperty("title").GetString().Should().Be("帳號或密碼不正確。");
        return problem.GetProperty("title").GetString()!;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode statusCode, string code)
    {
        string body = await response.Content.ReadAsStringAsync();
        JsonElement problem = JsonDocument.Parse(body).RootElement;
        response.StatusCode.Should().Be(statusCode, body);
        problem.GetProperty("code").GetString().Should().Be(code);
        problem.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        problem.TryGetProperty("accessToken", out _).Should().BeFalse();
        problem.TryGetProperty("refreshToken", out _).Should().BeFalse();
        body.Should().NotContain("System.").And.NotContain("Microsoft.Data.SqlClient");
    }

    private async Task<(Guid AccountId, string Account, string Email)> CreateUserAsync(string role, bool emailConfirmed)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string account = $"auth-{role.ToLowerInvariant()}-{suffix}";
        string email = $"auth-{role.ToLowerInvariant()}-{suffix}@example.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = account,
            Email = email,
            Name = $"{role} 測試帳號",
            EmailConfirmed = emailConfirmed,
            IsEnabled = true
        };

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await userManager.CreateAsync(user, ValidPassword)).Succeeded.Should().BeTrue();
        (await userManager.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
        db.UserPreferences.Add(new(user.Id));
        await db.SaveChangesAsync();
        Track(user.Id, email);
        return (user.Id, account, email);
    }

    private async Task SeedRefreshTokenAsync(
        Guid accountId, Guid familyId, string rawToken, DateTimeOffset expiresAt, bool revoked = false)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var token = new ProjectManagementWeb.Domain.Entities.RefreshToken(
            Guid.NewGuid(), accountId, familyId, HashRefreshToken(rawToken), now.AddMinutes(-2), expiresAt);
        if (revoked)
        {
            token.Revoke(now.AddMinutes(-1));
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync();
    }

    private async Task AssertFamilyRevokedAsync(Guid accountId, Guid familyId, int expectedCount)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var family = await db.RefreshTokens
            .Where(x => x.AccountId == accountId && x.FamilyId == familyId)
            .ToArrayAsync();
        family.Should().HaveCount(expectedCount).And.OnlyContain(token => token.RevokedAt != null);
    }

    private static string CreateRawRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    private static string NewClientAddress() => $"test-{Guid.NewGuid():N}";

    private static string CreateTestPrivateKeyPem()
    {
        using RSA rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private static string CreateAccessToken(
        RSA signingKey,
        Guid accountId,
        string issuer,
        string audience,
        DateTime expires)
    {
        DateTime notBefore = expires <= DateTime.UtcNow
            ? expires.AddMinutes(-5)
            : DateTime.UtcNow.AddMinutes(-1);
        Claim[] claims =
        [
            new(JwtRegisteredClaimNames.Sub, accountId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("name", "jwt-test"),
            new("role", SystemRoles.User),
            new("token_version", "0"),
            new("permission", SystemFunctions.ProjectsRead)
        ];
        var token = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            notBefore,
            expires,
            new SigningCredentials(new RsaSecurityKey(signingKey), SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string ExtractCookie(HttpResponseMessage response, string name)
    {
        string prefix = $"{name}=";
        string setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return setCookie[prefix.Length..setCookie.IndexOf(';')];
    }

    private static string HashRefreshToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(rawToken))));

    private static void AssertRefreshCookieCleared(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Should().Contain(value =>
            value.Contains("PMW-REFRESH=", StringComparison.Ordinal) &&
            value.Contains("expires=", StringComparison.OrdinalIgnoreCase));

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

    private async Task SetAccountEnabledAsync(Guid accountId, bool enabled)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ApplicationUser user = await db.Users.SingleAsync(x => x.Id == accountId);
        user.IsEnabled = enabled;
        await db.SaveChangesAsync();
    }

    private void Track(Guid accountId, string email)
    {
        _accountIds.Add(accountId);
        _emails.Add(email);
    }

    private async Task CleanupTestDataAsync()
    {
        if (_factory is null)
        {
            return;
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        string[] clientAddressHashes = _factory.ClientAddressProvider.UsedAddresses
            .Select(HashClientAddress)
            .ToArray();
        if (_loginAccountKeyHashes.Count > 0 || clientAddressHashes.Length > 0)
        {
            await db.LoginFailureAttempts
                .Where(x => _loginAccountKeyHashes.Contains(x.AccountKeyHash) ||
                            clientAddressHashes.Contains(x.ClientAddressHash))
                .ExecuteDeleteAsync();
        }
        await db.EmailVerificationResendAttempts
            .Where(x => clientAddressHashes.Contains(x.ClientAddressHash))
            .ExecuteDeleteAsync();
        if (_emails.Count > 0)
        {
            await db.EmailMessages.Where(x => _emails.Contains(x.Recipient)).ExecuteDeleteAsync();
        }
        if (_accountIds.Count > 0)
        {
            await db.Users.Where(x => _accountIds.Contains(x.Id)).ExecuteDeleteAsync();
        }
        _emails.Clear();
        _accountIds.Clear();
        _loginAccountKeyHashes.Clear();
    }

    private static string HashClientAddress(string clientAddress) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientAddress.Trim())));

    private static string HashLoginAccountKey(string account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Trim().ToUpperInvariant())));

    private enum CsrfFailureMode
    {
        MissingHeader,
        HeaderOnly,
        InvalidToken
    }
}
