using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ProjectManagementWeb.Api.Controllers;
using ProjectManagementWeb.Application.Auth;
using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.UnitTests;

public sealed class AuthControllerCookieTests
{
    [TestCase("Development", "http", false)]
    [TestCase("Development", "https", true)]
    [TestCase("Testing", "http", false)]
    [TestCase("Production", "http", true)]
    public async Task RefreshCookie應依環境與RequestScheme決定是否使用Secure(
        string environmentName,
        string requestScheme,
        bool expectedSecure)
    {
        var token = new AuthTokenResult(
            "access-token",
            DateTimeOffset.UtcNow.AddMinutes(15),
            "refresh-token",
            DateTimeOffset.UtcNow.AddDays(7));
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.LoginAsync(It.IsAny<LoginRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResult<AuthTokenResult>.Success(token));
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.EnvironmentName).Returns(environmentName);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = requestScheme;
        var controller = new AuthController(authService.Object, environment.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        await controller.Login(new LoginRequest("account", "password"), CancellationToken.None);

        string refreshCookie = httpContext.Response.Headers.SetCookie.ToString();
        refreshCookie.Should().StartWith("PMW-REFRESH=");
        refreshCookie.Contains("; secure", StringComparison.OrdinalIgnoreCase).Should().Be(expectedSecure);
    }
}
