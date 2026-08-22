using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

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
    public async Task OpenApi文件應包含Bearer與核心端點()
    {
        string document = await _client.GetStringAsync("/openapi/v1.json");

        document.Should().Contain("Bearer");
        document.Should().Contain("/api/v1/auth/login");
        document.Should().Contain("/api/v1/projects/{projectId}/task-items");
    }
}
