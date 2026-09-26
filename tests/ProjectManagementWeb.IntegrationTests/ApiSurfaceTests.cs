using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProjectManagementWeb.Api;

namespace ProjectManagementWeb.IntegrationTests;

public sealed class ApiSurfaceTests
{
    private const string TestConnectionString =
        "Server=localhost;Database=ProjectManagementWeb.Testing;User Id=not-used;Password=not-used;Encrypt=True;TrustServerCertificate=True";

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Environment", "Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection", TestConnectionString);
            builder.UseSetting("BootstrapAdmin:Account", string.Empty);
            builder.UseSetting("ReminderJobs:Enabled", "false");
        });
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    // 測試案例：TC-F-AUTH-020（僅 CSRF endpoint 與 cookie；部分覆蓋）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Csrf端點應回傳RequestToken並設定Cookie()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/v1/security/csrf-token");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("token");
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies).Should().BeTrue();
        cookies.Should().Contain(value => value.Contains("PMW-CSRF", StringComparison.Ordinal));
    }

    [TestCase("Development", CookieSecurePolicy.SameAsRequest)]
    [TestCase("Testing", CookieSecurePolicy.SameAsRequest)]
    [TestCase("Production", CookieSecurePolicy.Always)]
    public void CsrfCookieSecurePolicy應依環境設定(string environment, CookieSecurePolicy expectedPolicy)
    {
        using RSA rsa = RSA.Create(2048);
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:DefaultConnection", TestConnectionString);
            builder.UseSetting("BootstrapAdmin:Account", string.Empty);
            builder.UseSetting("Jwt:PrivateKeyPem", rsa.ExportPkcs8PrivateKeyPem());
            builder.UseSetting("ReminderJobs:Enabled", "false");
        });

        AntiforgeryOptions options = factory.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;

        options.Cookie.SecurePolicy.Should().Be(expectedPolicy);
    }

    // 測試案例：TC-ERR-AUTH-005（register 缺少 CSRF；部分覆蓋）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 註冊缺少CsrfToken時應拒絕請求()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            account = "test-user",
            password = "Test_password123!",
            email = "test@example.com"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // 測試案例：TC-ERR-AUTH-003（Backend API；與 Unit、Frontend 對應測試共同覆蓋）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 註冊欄位不正確時應回傳欄位錯誤ProblemDetails()
    {
        JsonElement csrf = await _client.GetFromJsonAsync<JsonElement>("/api/v1/security/csrf-token");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new
            {
                account = "invalid account",
                password = "weak",
                confirmPassword = "different",
                email = "invalid-email",
                name = " "
            })
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());

        HttpResponseMessage response = await _client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        JsonElement problem = JsonDocument.Parse(body).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        problem.GetProperty("code").GetString().Should().Be("validation_error");
        JsonElement errors = problem.GetProperty("errors");
        errors.TryGetProperty("account", out _).Should().BeTrue();
        errors.TryGetProperty("name", out _).Should().BeTrue();
        errors.TryGetProperty("email", out _).Should().BeTrue();
        errors.TryGetProperty("password", out _).Should().BeTrue();
        errors.TryGetProperty("confirmPassword", out _).Should().BeTrue();
    }

    // 測試案例：TC-F-API-001（OpenAPI 核心端點、request/response contract 與完整 enum 集合）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task OpenApi文件應包含Bearer與核心端點()
    {
        string document = await _client.GetStringAsync("/openapi/v1.json");

        document.Should().Contain("Bearer");
        document.Should().Contain("/api/v1/auth/login");
        document.Should().Contain("/api/v1/projects/{projectId}/task-items");
        document.Should().Contain("/api/v1/users/{id}/administration");
        document.Should().Contain("/api/v1/users/me/profile");
        document.Should().Contain("/api/v1/projects/{id}/member-candidates");

        using JsonDocument openApi = JsonDocument.Parse(document);
        JsonElement schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
        schemas.GetProperty("CreateProjectRequest").GetProperty("properties").TryGetProperty("code", out _)
            .Should().BeFalse();
        schemas.GetProperty("CreateTaskRequest").GetProperty("properties").TryGetProperty("code", out _)
            .Should().BeFalse();
        JsonElement projectResponseProperties = schemas.GetProperty("ProjectResponse").GetProperty("properties");
        projectResponseProperties.TryGetProperty("versionNumber", out _).Should().BeTrue();
        projectResponseProperties.TryGetProperty("rowVersion", out _).Should().BeTrue();
        GetEnumValues(schemas, "ProjectStatus").Should().Equal("Pending", "Active", "Completed", "Archived");
        GetEnumValues(schemas, "TaskStatus").Should().Equal("Pending", "InProgress", "Blocked", "Completed");
    }

    // 測試案例：TC-F-API-004（Health、允許／拒絕 origin 與 credentials）
    // 測試結果：Passed
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task Health與Cors應只允許設定的Origin並允許Credentials()
    {
        (await _client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Options, "/api/v1/projects");
        allowedRequest.Headers.Add("Origin", "http://localhost:5173");
        allowedRequest.Headers.Add("Access-Control-Request-Method", "GET");
        HttpResponseMessage allowed = await _client.SendAsync(allowedRequest);
        allowed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        allowed.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle()
            .Which.Should().Be("http://localhost:5173");
        allowed.Headers.GetValues("Access-Control-Allow-Credentials").Should().ContainSingle()
            .Which.Should().Be("true");

        using var deniedRequest = new HttpRequestMessage(HttpMethod.Options, "/api/v1/projects");
        deniedRequest.Headers.Add("Origin", "https://untrusted.example.test");
        deniedRequest.Headers.Add("Access-Control-Request-Method", "GET");
        HttpResponseMessage denied = await _client.SendAsync(deniedRequest);
        denied.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        denied.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    // 測試案例：TC-F-API-004（OpenAPI 環境與明確開關）
    // 測試結果：Passed（4 組環境／設定組合）
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [TestCase("Development", null, HttpStatusCode.OK)]
    [TestCase("Testing", null, HttpStatusCode.OK)]
    [TestCase("Production", null, HttpStatusCode.NotFound)]
    [TestCase("Production", "true", HttpStatusCode.OK)]
    public async Task OpenApi應只在允許環境或明確開啟時暴露(
        string environment,
        string? enabled,
        HttpStatusCode expected)
    {
        using RSA rsa = RSA.Create(2048);
        string privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:DefaultConnection", TestConnectionString);
            builder.UseSetting("BootstrapAdmin:Account", string.Empty);
            builder.UseSetting("Jwt:PrivateKeyPem", privateKeyPem);
            builder.UseSetting("ReminderJobs:Enabled", "false");
            if (enabled is not null) builder.UseSetting("OpenApi:Enabled", enabled);
        });
        using HttpClient client = factory.CreateClient();

        (await client.GetAsync("/openapi/v1.json")).StatusCode.Should().Be(expected);
        (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode.Should().Be(expected);
    }

    private static IEnumerable<string> GetEnumValues(JsonElement schemas, string schemaName) =>
        schemas.GetProperty(schemaName).GetProperty("enum").EnumerateArray().Select(value => value.GetString()!);
}
