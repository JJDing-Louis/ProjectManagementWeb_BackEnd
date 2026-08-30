using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectManagementWeb.Application.Auth;
using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.Api.Controllers;

[Route("api/v1/auth")]
public sealed class AuthController : ApiControllerBase
{
    private const string RefreshCookieName = "PMW-REFRESH";
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService) => _authService = authService;

    [AllowAnonymous, ValidateAntiForgeryToken]
    [HttpPost("register")]
    public async Task<ActionResult<RegisterResponse>> Register(
        RegisterRequest request,
        CancellationToken cancellationToken)
    {
        ServiceResult<RegisterResponse> result = await _authService.RegisterAsync(request, cancellationToken);
        if (!result.IsSuccess)
        {
            return FromError(result.Error!);
        }
        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    [AllowAnonymous, ValidateAntiForgeryToken]
    [HttpPost("login")]
    public async Task<ActionResult<object>> Login(LoginRequest request, CancellationToken cancellationToken) =>
        await IssueTokensAsync(await _authService.LoginAsync(request, cancellationToken));

    [AllowAnonymous, ValidateAntiForgeryToken]
    [HttpPost("refresh")]
    public async Task<ActionResult<object>> Refresh(CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue(RefreshCookieName, out string? token) || string.IsNullOrWhiteSpace(token))
        {
            return UnauthorizedProblem("missing_refresh_token", "缺少 Refresh Token Cookie。");
        }
        return await IssueTokensAsync(await _authService.RefreshAsync(token, cancellationToken));
    }

    [AllowAnonymous, ValidateAntiForgeryToken]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        Request.Cookies.TryGetValue(RefreshCookieName, out string? token);
        await _authService.LogoutAsync(token, cancellationToken);
        Response.Cookies.Delete(RefreshCookieName, RefreshCookieOptions(DateTimeOffset.UtcNow));
        return NoContent();
    }

    [AllowAnonymous, ValidateAntiForgeryToken]
    [HttpPost("email/confirm")]
    public async Task<ActionResult<bool>> ConfirmEmail(ConfirmEmailRequest request, CancellationToken cancellationToken) =>
        FromResult(await _authService.ConfirmEmailAsync(request, cancellationToken));

    [AllowAnonymous, ValidateAntiForgeryToken]
    [HttpPost("email/resend")]
    public async Task<ActionResult<bool>> ResendEmail(ResendEmailRequest request, CancellationToken cancellationToken) =>
        FromResult(await _authService.ResendEmailAsync(request, cancellationToken));

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<CurrentAccountResponse>> Me(CancellationToken cancellationToken) =>
        FromResult(await _authService.GetCurrentAsync(cancellationToken));

    private Task<ActionResult<object>> IssueTokensAsync(ServiceResult<AuthTokenResult> result)
    {
        if (!result.IsSuccess)
        {
            return Task.FromResult<ActionResult<object>>(FromResult(result));
        }
        AuthTokenResult token = result.Value!;
        Response.Cookies.Append(RefreshCookieName, token.RefreshToken, RefreshCookieOptions(token.RefreshTokenExpiresAt));
        return Task.FromResult<ActionResult<object>>(Ok(new
        {
            token.AccessToken,
            token.AccessTokenExpiresAt
        }));
    }

    private CookieOptions RefreshCookieOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/api/v1/auth",
        Expires = expires
    };

    private ActionResult<object> UnauthorizedProblem(string code, string title)
    {
        var details = new ProblemDetails { Status = 401, Title = title, Instance = Request.Path };
        details.Extensions["code"] = code;
        return new ObjectResult(details) { StatusCode = 401 };
    }
}
