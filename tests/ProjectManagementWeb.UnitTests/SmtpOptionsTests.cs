using FluentAssertions;
using ProjectManagementWeb.Infrastructure.Configuration;

namespace ProjectManagementWeb.UnitTests;

public sealed class SmtpOptionsTests
{
    [Test]
    public void 憑證撤銷檢查預設應啟用()
    {
        new SmtpOptions().CheckCertificateRevocation.Should().BeTrue();
    }
}
