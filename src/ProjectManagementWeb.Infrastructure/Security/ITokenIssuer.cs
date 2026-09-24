using ProjectManagementWeb.Infrastructure.Identity;

namespace ProjectManagementWeb.Infrastructure.Security;

internal interface ITokenIssuer
{
    (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(ApplicationUser user, string role, IReadOnlyCollection<string> functions);
    (string RawToken, string Hash, DateTimeOffset ExpiresAt) CreateRefreshToken();
    string HashRefreshToken(string rawToken);
}
