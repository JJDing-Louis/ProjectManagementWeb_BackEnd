using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ProjectManagementWeb.Infrastructure.Configuration;
using ProjectManagementWeb.Infrastructure.Identity;

namespace ProjectManagementWeb.Infrastructure.Security;

internal sealed class JwtTokenIssuer : ITokenIssuer, IDisposable
{
    private readonly JwtOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly RSA _rsa;

    public JwtTokenIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider, IHostEnvironment environment)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _rsa = RSA.Create(2048);

        if (!string.IsNullOrWhiteSpace(_options.PrivateKeyPem))
        {
            _rsa.ImportFromPem(_options.PrivateKeyPem.Replace("\\n", "\n", StringComparison.Ordinal));
        }
        else if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException("正式環境必須設定 Jwt:PrivateKeyPem。");
        }
    }

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(
        ApplicationUser user, string role, IReadOnlyCollection<string> functions)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset expiresAt = now.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("name", user.UserName ?? string.Empty),
            new("role", role),
            new(TokenClaims.TokenVersion, user.TokenVersion.ToString())
        };
        claims.AddRange(functions.Select(function => new Claim(TokenClaims.Permission, function)));

        var credentials = new SigningCredentials(new RsaSecurityKey(_rsa), SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            now.UtcDateTime,
            expiresAt.UtcDateTime,
            credentials);
        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public (string RawToken, string Hash, DateTimeOffset ExpiresAt) CreateRefreshToken()
    {
        string rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        return (rawToken, HashRefreshToken(rawToken), _timeProvider.GetUtcNow().AddDays(_options.RefreshTokenDays));
    }

    public string HashRefreshToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public SecurityKey ValidationKey => new RsaSecurityKey(_rsa);

    public void Dispose() => _rsa.Dispose();
}
