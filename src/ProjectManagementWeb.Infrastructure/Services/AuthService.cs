using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectManagementWeb.Application.Auth;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Domain.Enums;
using ProjectManagementWeb.Infrastructure.Configuration;
using ProjectManagementWeb.Infrastructure.Email;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;
using ProjectManagementWeb.Infrastructure.Security;

namespace ProjectManagementWeb.Infrastructure.Services;

internal sealed class AuthService : IAuthService
{
    private static readonly TimeSpan VerificationTokenLifetime = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ResendLimitWindow = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan LoginFailureLimitWindow = TimeSpan.FromMinutes(15);
    private const int ResendLimit = 5;
    private const int LoginFailureLimit = 5;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ITokenIssuer _tokenIssuer;
    private readonly IEmailGateway _emailGateway;
    private readonly ILogger<AuthService> _logger;
    private readonly SmtpOptions _smtpOptions;
    private readonly TimeProvider _timeProvider;
    private readonly IClientAddressProvider _clientAddressProvider;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        ICurrentUser currentUser,
        ITokenIssuer tokenIssuer,
        IEmailGateway emailGateway,
        ILogger<AuthService> logger,
        IOptions<SmtpOptions> smtpOptions,
        TimeProvider timeProvider,
        IClientAddressProvider clientAddressProvider)
    {
        _userManager = userManager;
        _db = db;
        _currentUser = currentUser;
        _tokenIssuer = tokenIssuer;
        _emailGateway = emailGateway;
        _logger = logger;
        _smtpOptions = smtpOptions.Value;
        _timeProvider = timeProvider;
        _clientAddressProvider = clientAddressProvider;
    }

    public async Task<ServiceResult<RegisterResponse>> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string[]> validationErrors = RegistrationValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return ServiceResult<RegisterResponse>.Failure(
                "validation_error",
                "註冊資料有誤，請修正標示的欄位。",
                400,
                validationErrors);
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = request.Account.Trim(),
            Email = request.Email.Trim(),
            Name = request.Name.Trim(),
            IsEnabled = true,
            EmailConfirmed = false
        };
        IdentityResult created;
        try
        {
            created = await _userManager.CreateAsync(user, request.Password);
        }
        catch (DbUpdateException exception) when (IsDuplicateEmailConstraint(exception))
        {
            return DuplicateEmailFailure();
        }
        if (!created.Succeeded)
        {
            if (created.Errors.Any(error => error.Code == "DuplicateEmail"))
            {
                return DuplicateEmailFailure();
            }
            return ServiceResult<RegisterResponse>.Failure(
                "registration_failed",
                "註冊資料無法建立，請修正標示的欄位。",
                400,
                MapIdentityErrors(created.Errors));
        }

        IdentityResult roleAdded = await _userManager.AddToRoleAsync(user, SystemRoles.Viewer);
        if (!roleAdded.Succeeded)
        {
            await _userManager.DeleteAsync(user);
            return ServiceResult<RegisterResponse>.Failure("registration_failed", "無法建立預設角色。", 500);
        }

        _db.UserPreferences.Add(new UserPreference(user.Id));
        await _db.SaveChangesAsync(cancellationToken);
        bool verificationEmailSent = await SendVerificationEmailAsync(user, cancellationToken);
        return ServiceResult<RegisterResponse>.Success(new RegisterResponse(user.Id, verificationEmailSent));
    }

    private static ServiceResult<RegisterResponse> DuplicateEmailFailure() =>
        ServiceResult<RegisterResponse>.Failure(
            "duplicate_email",
            "此 Email 已被使用。",
            409,
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = ["此 Email 已被使用。"]
            });

    private static bool IsDuplicateEmailConstraint(DbUpdateException exception) =>
        exception.InnerException is SqlException sqlException &&
        sqlException.Number is 2601 or 2627 &&
        sqlException.Message.Contains("EmailIndex", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string[]> MapIdentityErrors(IEnumerable<IdentityError> identityErrors)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (IdentityError error in identityErrors)
        {
            (string field, string message) = error.Code switch
            {
                "DuplicateUserName" => ("account", "此帳號已被使用。"),
                "InvalidUserName" => ("account", "帳號格式不正確。"),
                "DuplicateEmail" => ("email", "此 Email 已被使用。"),
                "InvalidEmail" => ("email", "Email 格式不正確。"),
                "PasswordTooShort" => ("password", $"密碼至少需要 {RegistrationRules.PasswordMinLength} 個字元。"),
                "PasswordRequiresUpper" => ("password", "密碼至少需要一個大寫英文字母。"),
                "PasswordRequiresLower" => ("password", "密碼至少需要一個小寫英文字母。"),
                "PasswordRequiresDigit" => ("password", "密碼至少需要一個數字。"),
                "PasswordRequiresNonAlphanumeric" => ("password", "密碼至少需要一個特殊字元。"),
                _ => ("registration", "註冊資料不符合系統規則。")
            };
            if (!errors.TryGetValue(field, out List<string>? messages))
            {
                messages = [];
                errors[field] = messages;
            }
            if (!messages.Contains(message, StringComparer.Ordinal))
            {
                messages.Add(message);
            }
        }
        return errors.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ServiceResult<AuthTokenResult>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset windowStart = now.Subtract(LoginFailureLimitWindow);
        string accountKeyHash = HashLoginAccountKey(request.Account);
        string clientAddressHash = HashClientAddress(_clientAddressProvider.GetClientAddress());

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        int accountFailures = await _db.LoginFailureAttempts.CountAsync(
            attempt => attempt.AccountKeyHash == accountKeyHash &&
                       attempt.Outcome == LoginFailureOutcome.InvalidCredentials &&
                       attempt.OccurredAt > windowStart &&
                       attempt.OccurredAt <= now,
            cancellationToken);
        int clientAddressFailures = await _db.LoginFailureAttempts.CountAsync(
            attempt => attempt.ClientAddressHash == clientAddressHash &&
                       attempt.Outcome == LoginFailureOutcome.InvalidCredentials &&
                       attempt.OccurredAt > windowStart &&
                       attempt.OccurredAt <= now,
            cancellationToken);
        if (accountFailures >= LoginFailureLimit || clientAddressFailures >= LoginFailureLimit)
        {
            await RecordLoginFailureAsync(
                accountKeyHash,
                clientAddressHash,
                now,
                LoginFailureOutcome.RateLimited,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<AuthTokenResult>.Failure("rate_limited", "請稍後再試。", 429);
        }

        ApplicationUser? user = await _userManager.FindByNameAsync(request.Account.Trim());
        if (user is null || !user.IsEnabled || !await _userManager.CheckPasswordAsync(user, request.Password))
        {
            await RecordLoginFailureAsync(
                accountKeyHash,
                clientAddressHash,
                now,
                LoginFailureOutcome.InvalidCredentials,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return accountFailures + 1 >= LoginFailureLimit || clientAddressFailures + 1 >= LoginFailureLimit
                ? ServiceResult<AuthTokenResult>.Failure("rate_limited", "請稍後再試。", 429)
                : ServiceResult<AuthTokenResult>.Failure("invalid_credentials", "帳號或密碼不正確。", 401);
        }

        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<AuthTokenResult>.Success(await IssueTokenPairAsync(user, null, cancellationToken));
    }

    private async Task RecordLoginFailureAsync(
        string accountKeyHash,
        string clientAddressHash,
        DateTimeOffset occurredAt,
        LoginFailureOutcome outcome,
        CancellationToken cancellationToken)
    {
        var attempt = new LoginFailureAttempt(
            Guid.NewGuid(),
            accountKeyHash,
            clientAddressHash,
            occurredAt,
            outcome);
        _db.LoginFailureAttempts.Add(attempt);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(
            "登入安全稽核已記錄。LoginFailureAttemptId: {LoginFailureAttemptId}, Outcome: {Outcome}",
            attempt.Id,
            outcome);
    }

    private static string HashLoginAccountKey(string account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Trim().ToUpperInvariant())));

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
        if (user is null || user.EmailConfirmed)
        {
            return ServiceResult<bool>.Failure("invalid_email_token", "驗證連結無效或已過期。", 400);
        }

        byte[] tokenBytes;
        try
        {
            tokenBytes = WebEncoders.Base64UrlDecode(request.Token);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return ServiceResult<bool>.Failure("invalid_email_token", "驗證連結無效或已過期。", 400);
        }

        if (tokenBytes.Length != 32)
        {
            return ServiceResult<bool>.Failure("invalid_email_token", "驗證連結無效或已過期。", 400);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        string tokenHash = HashVerificationToken(request.Token);
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        EmailVerificationToken? verificationToken = await _db.EmailVerificationTokens
            .SingleOrDefaultAsync(
                x => x.AccountId == user.Id && x.TokenHash == tokenHash,
                cancellationToken);
        if (verificationToken is null || !verificationToken.IsActive(now))
        {
            return ServiceResult<bool>.Failure("invalid_email_token", "驗證連結無效或已過期。", 400);
        }

        verificationToken.Use(now);
        user.EmailConfirmed = true;
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<bool>> ResendEmailAsync(ResendEmailRequest request, CancellationToken cancellationToken)
    {
        string normalized = request.AccountOrEmail.Trim();
        ApplicationUser? user = await _userManager.FindByNameAsync(normalized) ?? await _userManager.FindByEmailAsync(normalized);
        EmailVerificationResendOutcome outcome = await RecordResendAttemptAsync(user, cancellationToken);
        if (outcome == EmailVerificationResendOutcome.IpLimited)
        {
            return ServiceResult<bool>.Failure("rate_limited", "請稍後再試。", 429);
        }

        if (outcome == EmailVerificationResendOutcome.Allowed)
        {
            await SendVerificationEmailAsync(user!, cancellationToken);
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

    private async Task<bool> SendVerificationEmailAsync(
        ApplicationUser user,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        string rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        string url = $"{_smtpOptions.FrontendBaseUrl.TrimEnd('/')}/verify-email?accountId={user.Id}&token={Uri.EscapeDataString(rawToken)}";
        var email = new EmailMessage(Guid.NewGuid(), user.Email ?? string.Empty, "驗證您的帳號",
            $"<p>請點擊以下連結完成 Email 驗證：</p><p><a href=\"{url}\">驗證帳號</a></p>", now);
        var verificationToken = new EmailVerificationToken(
            Guid.NewGuid(),
            user.Id,
            email.Id,
            HashVerificationToken(rawToken),
            now,
            now.Add(VerificationTokenLifetime));
        _db.EmailMessages.Add(email);
        _db.EmailVerificationTokens.Add(verificationToken);
        await _db.SaveChangesAsync(cancellationToken);
        try
        {
            await _emailGateway.SendAsync(
                email.Recipient,
                email.Subject,
                email.Body,
                email.Id.ToString("N"),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            verificationToken.Invalidate(_timeProvider.GetUtcNow());
            email.MarkFailed(exception.Message);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(
                "驗證信寄送失敗，已記錄失敗狀態。EmailMessageId: {EmailMessageId}, AccountId: {AccountId}, FailureType: {FailureType}",
                email.Id,
                user.Id,
                exception.GetType().Name);
            return false;
        }

        DateTimeOffset sentAt = _timeProvider.GetUtcNow();
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        EmailVerificationToken[] activeTokens = await _db.EmailVerificationTokens
            .Where(x =>
                x.AccountId == user.Id &&
                x.Id != verificationToken.Id &&
                x.ActivatedAt != null &&
                x.UsedAt == null &&
                x.InvalidatedAt == null)
            .ToArrayAsync(cancellationToken);
        foreach (EmailVerificationToken activeToken in activeTokens)
        {
            activeToken.Invalidate(sentAt);
        }

        await _db.SaveChangesAsync(cancellationToken);
        verificationToken.Activate(sentAt);
        email.MarkSent(sentAt);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static string HashVerificationToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    private async Task<EmailVerificationResendOutcome> RecordResendAttemptAsync(
        ApplicationUser? user,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset windowStart = now.Subtract(ResendLimitWindow);
        string clientAddressHash = HashClientAddress(_clientAddressProvider.GetClientAddress());

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        int ipAttemptCount = await _db.EmailVerificationResendAttempts.CountAsync(
            x => x.ClientAddressHash == clientAddressHash &&
                 x.RequestedAt > windowStart &&
                 x.RequestedAt <= now,
            cancellationToken);

        EmailVerificationResendOutcome outcome;
        if (ipAttemptCount >= ResendLimit)
        {
            outcome = EmailVerificationResendOutcome.IpLimited;
        }
        else if (user is null || user.EmailConfirmed || !user.IsEnabled)
        {
            outcome = EmailVerificationResendOutcome.NotEligible;
        }
        else
        {
            EmailVerificationResendAttempt? lastAllowed = await _db.EmailVerificationResendAttempts
                .Where(x =>
                    x.AccountId == user.Id &&
                    x.Outcome == EmailVerificationResendOutcome.Allowed &&
                    x.RequestedAt <= now)
                .OrderByDescending(x => x.RequestedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (lastAllowed is not null && now - lastAllowed.RequestedAt < ResendCooldown)
            {
                outcome = EmailVerificationResendOutcome.CooldownLimited;
            }
            else
            {
                int accountAllowedCount = await _db.EmailVerificationResendAttempts.CountAsync(
                    x => x.AccountId == user.Id &&
                         x.Outcome == EmailVerificationResendOutcome.Allowed &&
                         x.RequestedAt > windowStart &&
                         x.RequestedAt <= now,
                    cancellationToken);
                outcome = accountAllowedCount >= ResendLimit
                    ? EmailVerificationResendOutcome.AccountLimited
                    : EmailVerificationResendOutcome.Allowed;
            }
        }

        _db.EmailVerificationResendAttempts.Add(new EmailVerificationResendAttempt(
            Guid.NewGuid(),
            user?.Id,
            clientAddressHash,
            now,
            outcome));
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    private static string HashClientAddress(string clientAddress) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientAddress.Trim())));
}
