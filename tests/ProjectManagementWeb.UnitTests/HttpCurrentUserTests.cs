using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Bogus;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using ProjectManagementWeb.Api.Security;

namespace ProjectManagementWeb.UnitTests;

public sealed class HttpCurrentUserTests
{
    [Test]
    public void 已驗證的Claims應正確映射目前使用者與功能權限()
    {
        Randomizer.Seed = new Random(20260823);
        Guid accountId = Guid.NewGuid();
        string function = new Faker().Random.AlphaNumeric(12);
        var identity = new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, accountId.ToString()),
            new Claim("role", "User"),
            new Claim("function", function)
        ], "Test");
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(x => x.HttpContext).Returns(context);

        var currentUser = new HttpCurrentUser(accessor.Object);

        currentUser.IsAuthenticated.Should().BeTrue();
        currentUser.AccountId.Should().Be(accountId);
        currentUser.Role.Should().Be("User");
        currentUser.HasFunction(function).Should().BeTrue();
        currentUser.HasFunction("not-granted").Should().BeFalse();
    }
}
