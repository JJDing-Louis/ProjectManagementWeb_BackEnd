using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProjectManagementWeb.Application.Auth;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Infrastructure.Configuration;
using ProjectManagementWeb.Infrastructure.Email;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;
using ProjectManagementWeb.Infrastructure.Security;

namespace ProjectManagementWeb.Infrastructure.Services;

internal sealed class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ITokenIssuer _tokenIssuer;
    private readonly IEmailGateway _emailGateway;
    private readonly SmtpOptions _smtpOptions;
    private readonly TimeProvider _timeProvider;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        ICurrentUser currentUser,
        ITokenIssuer tokenIssuer,
        IEmailGateway emailGateway,
        IOptions<SmtpOptions> smtpOptions,
        TimeProvider timeProvider)
    {
        _userManager = userManager;
        _db = db;
        _currentUser = currentUser;
        _tokenIssuer = tokenIssuer;
        _emailGateway = emailGateway;
        _smtpOptions = smtpOptions.Value;
        _timeProvider = timeProvider;
    }

    public async Task<ServiceResult<Guid>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Account) || string.IsNullOrWhiteSpace(request.Email))
        {
            return ServiceResult<Guid>.Failure("validation_error", "帳號與 Email 為必填。", 400);
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = request.Account.Trim(),
            Email = request.Email.Trim(),
            Name = request.Name?.Trim(),
            IsEnabled = true,
            EmailConfirmed = false
        };
        IdentityResult created = await _userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            string message = string.Join(" ", created.Errors.Select(x => x.Description));
            return ServiceResult<Guid>.Failure("registration_failed", message, 400);
        }

        IdentityResult roleAdded = await _userManager.AddToRoleAsync(user, SystemRoles.Viewer);
        if (!roleAdded.Succeeded)
        {
            await _userManager.DeleteAsync(user);
            return ServiceResult<Guid>.Failure("registration_failed", "無法建立預設角色。", 500);
        }

        _db.UserPreferences.Add(new UserPreference(user.Id));
        await _db.SaveChangesAsync(cancellationToken);
        await SendVerificationEmailAsync(user, cancellationToken);
        return ServiceResult<Guid>.Success(user.Id);
    }

    public async Task<ServiceResult<AuthTokenResult>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ApplicationUser? user = await _userManager.FindByNameAsync(request.Account.Trim());
        if (user is null || !user.IsEnabled || !await _userManager.CheckPasswordAsync(user, request.Password))
        {
            return ServiceResult<AuthTokenResult>.Failure("invalid_credentials", "帳號或密碼不正確。", 401);
        }

        return ServiceResult<AuthTokenResult>.Success(await IssueTokenPairAsync(user, null, cancellationToken));
    }

    public async Task<ServiceResult<AuthTokenResult>> RefreshAsync(string rawRefreshToken, CancellationToken cancellationToken)
    {
        string hash = _tokenIssuer.HashRefreshToken(rawRefreshToken);
        RefreshToken? existing = await _db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
        if (existing is null)
        {
            return ServiceResult<AuthTokenResult>.Failure("invalid_refresh_token", "Refresh token 無效。", 401);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (!existing.IsActive(now))
        {
            List<RefreshToken> family = await _db.RefreshTokens
                .Where(x => x.FamilyId == existing.FamilyId && x.RevokedAt == null)
                .ToListAsync(cancellationToken);
            foreach (RefreshToken token in family)
            {
                token.Revoke(now);
            }
            await _db.SaveChangesAsync(cancellationToken);
            return ServiceResult<AuthTokenResult>.Failure("refresh_token_reuse", "Refresh token 已失效。", 401);
        }

        ApplicationUser? user = await _userManager.FindByIdAsync(existing.AccountId.ToString());
        if (user is null || !user.IsEnabled)
        {
            existing.Revoke(now);
            await _db.SaveChangesAsync(cancellationToken);
            return ServiceResult<AuthTokenResult>.Failure("account_disabled", "帳號不可使用。", 401);
        }

        return ServiceResult<AuthTokenResult>.Success(await IssueTokenPairAsync(user, existing, cancellationToken));
    }

    public async Task LogoutAsync(string? rawRefreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return;
        }
        string hash = _tokenIssuer.HashRefreshToken(rawRefreshToken);
        RefreshToken? token = await _db.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
        if (token is not null)
        {
            token.Revoke(_timeProvider.GetUtcNow());
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<ServiceResult<bool>> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken)
    {
        ApplicationUser? user = await _userManager.FindByIdAsync(request.AccountId.ToString());
        if (user is null)
        {
            return ServiceResult<bool>.Failure("invalid_email_token", "驗證連結無效或已過期。", 400);
        }

        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Token));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return ServiceResult<bool>.Failure("invalid_email_token", "驗證連結無效或已過期。", 400);
        }

        IdentityResult result = await _userManager.ConfirmEmailAsync(user, token);
        return result.Succeeded
            ? ServiceResult<bool>.Success(true)
            : ServiceResult<bool>.Failure("invalid_email_token", "驗證連結無效或已過期。", 400);
    }

    public async Task<ServiceResult<bool>> ResendEmailAsync(ResendEmailRequest request, CancellationToken cancellationToken)
    {
        string normalized = request.AccountOrEmail.Trim();
        ApplicationUser? user = await _userManager.FindByNameAsync(normalized) ?? await _userManager.FindByEmailAsync(normalized);
        if (user is not null && !user.EmailConfirmed && user.IsEnabled)
        {
            await SendVerificationEmailAsync(user, cancellationToken);
        }
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<CurrentAccountResponse>> GetCurrentAsync(CancellationToken cancellationToken)
    {
        if (_currentUser.AccountId is not Guid id)
        {
            return ServiceResult<CurrentAccountResponse>.Failure("unauthorized", "尚未登入。", 401);
        }
        ApplicationUser? user = await _userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return ServiceResult<CurrentAccountResponse>.Failure("not_found", "找不到帳號。", 404);
        }
        (string role, IReadOnlyCollection<string> functions) = await GetAuthorizationAsync(user, cancellationToken);
        return ServiceResult<CurrentAccountResponse>.Success(new CurrentAccountResponse(
            user.Id, user.UserName ?? string.Empty, user.Email ?? string.Empty, user.Name,
            user.EmailConfirmed, user.IsEnabled, role, functions));
    }

    private async Task<AuthTokenResult> IssueTokenPairAsync(
        ApplicationUser user, RefreshToken? existing, CancellationToken cancellationToken)
    {
        (string role, IReadOnlyCollection<string> functions) = await GetAuthorizationAsync(user, cancellationToken);
        var access = _tokenIssuer.CreateAccessToken(user, role, functions);
        var refresh = _tokenIssuer.CreateRefreshToken();
        Guid familyId = existing?.FamilyId ?? Guid.NewGuid();
        var refreshEntity = new RefreshToken(Guid.NewGuid(), user.Id, familyId, refresh.Hash,
            _timeProvider.GetUtcNow(), refresh.ExpiresAt);

        if (existing is not null)
        {
            existing.Revoke(_timeProvider.GetUtcNow(), refreshEntity.Id);
        }
        _db.RefreshTokens.Add(refreshEntity);
        await _db.SaveChangesAsync(cancellationToken);
        return new AuthTokenResult(access.Token, access.ExpiresAt, refresh.RawToken, refresh.ExpiresAt);
    }

    private async Task<(string Role, IReadOnlyCollection<string> Functions)> GetAuthorizationAsync(
        ApplicationUser user, CancellationToken cancellationToken)
    {
        IList<string> roles = await _userManager.GetRolesAsync(user);
        string role = user.EmailConfirmed ? roles.SingleOrDefault() ?? SystemRoles.Viewer : SystemRoles.Viewer;
        Guid roleId = SeedIds.Create($"role:{role}");
        string[] functions = await (
            from mapping in _db.RoleFunctions
            join function in _db.Functions on mapping.FunctionId equals function.Id
            where mapping.RoleId == roleId
            select function.Code).ToArrayAsync(cancellationToken);
        return (role, functions);
    }

    private async Task SendVerificationEmailAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        string rawToken = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        string encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(rawToken));
        string url = $"{_smtpOptions.FrontendBaseUrl.TrimEnd('/')}/verify-email?accountId={user.Id}&token={Uri.EscapeDataString(encodedToken)}";
        var email = new EmailMessage(Guid.NewGuid(), user.Email ?? string.Empty, "驗證您的帳號",
            $"<p>請點擊以下連結完成 Email 驗證：</p><p><a href=\"{url}\">驗證帳號</a></p>", _timeProvider.GetUtcNow());
        _db.EmailMessages.Add(email);
        await _db.SaveChangesAsync(cancellationToken);
        try
        {
            await _emailGateway.SendAsync(email.Recipient, email.Subject, email.Body, cancellationToken);
            email.MarkSent(_timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            email.MarkFailed(exception.Message);
        }
        await _db.SaveChangesAsync(cancellationToken);
    }
}
