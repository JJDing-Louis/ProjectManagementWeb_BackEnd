using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ProjectManagementWeb.Infrastructure.Configuration;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.Infrastructure.Security;

internal sealed class JwtBearerOptionsSetup : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly JwtTokenIssuer _issuer;
    private readonly JwtOptions _options;

    public JwtBearerOptionsSetup(JwtTokenIssuer issuer, IOptions<JwtOptions> options)
    {
        _issuer = issuer;
        _options = options.Value;
    }

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _issuer.ValidationKey,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = "role"
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ValidateAccountAsync
        };
    }

    public void Configure(JwtBearerOptions options) =>
        Configure(JwtBearerDefaults.AuthenticationScheme, options);

    private static async Task ValidateAccountAsync(TokenValidatedContext context)
    {
        string? subject = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        string? versionClaim = context.Principal?.FindFirst(TokenClaims.TokenVersion)?.Value;
        if (!Guid.TryParse(subject, out Guid accountId) || !int.TryParse(versionClaim, out int tokenVersion))
        {
            context.Fail("Token claims 無效。");
            return;
        }

        ApplicationDbContext db = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        bool valid = await db.Users.AsNoTracking().AnyAsync(
            user => user.Id == accountId && user.IsEnabled && user.TokenVersion == tokenVersion,
            context.HttpContext.RequestAborted);
        if (!valid)
        {
            context.Fail("帳號已停用或 Token 已撤銷。");
        }
    }
}
