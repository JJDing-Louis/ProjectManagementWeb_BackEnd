using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ProjectManagementWeb.Api;

namespace ProjectManagementWeb.IntegrationTests;

public sealed class ApiSurfaceTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Environment", "Testing"));
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

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

    [Test]
    public async Task OpenApi文件應包含Bearer與核心端點()
    {
        string document = await _client.GetStringAsync("/openapi/v1.json");

        document.Should().Contain("Bearer");
        document.Should().Contain("/api/v1/auth/login");
        document.Should().Contain("/api/v1/projects/{projectId}/task-items");
        document.Should().Contain("/api/v1/users/{id}/administration");
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
    }
}
