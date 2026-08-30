using FluentAssertions;
using ProjectManagementWeb.Application.Users;

namespace ProjectManagementWeb.UnitTests;

public sealed class BootstrapAdminPolicyTests
{
    [Test]
    public void IsBootstrapAdmin_設定帳號時應忽略大小寫與前後空白()
    {
        var policy = new BootstrapAdminPolicy(" admin ");

        policy.IsBootstrapAdmin("ADMIN").Should().BeTrue();
        policy.NormalizedAccount.Should().Be("ADMIN");
    }

    [Test]
    public void IsBootstrapAdmin_未設定帳號時不應鎖定一般帳號()
    {
        var policy = new BootstrapAdminPolicy(null);

        policy.IsBootstrapAdmin("admin").Should().BeFalse();
        policy.NormalizedAccount.Should().BeNull();
    }
}
