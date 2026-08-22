namespace ProjectManagementWeb.Application.Auth;

public sealed record RegisterRequest(string Account, string Password, string Email, string? Name);
public sealed record LoginRequest(string Account, string Password);
public sealed record ConfirmEmailRequest(Guid AccountId, string Token);
public sealed record ResendEmailRequest(string AccountOrEmail);
public sealed record AuthTokenResult(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);
public sealed record CurrentAccountResponse(Guid Id, string Account, string Email, string? Name, bool EmailConfirmed, bool IsEnabled, string Role, IReadOnlyCollection<string> Functions);
