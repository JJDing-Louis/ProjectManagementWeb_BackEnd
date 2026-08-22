namespace ProjectManagementWeb.Infrastructure.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "ProjectManagementWeb";
    public string Audience { get; set; } = "ProjectManagementWeb.Spa";
    public string? PrivateKeyPem { get; set; }
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
}
