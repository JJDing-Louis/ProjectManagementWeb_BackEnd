using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.Application.Auth;

public interface IAuthService
{
    Task<ServiceResult<Guid>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<AuthTokenResult>> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<AuthTokenResult>> RefreshAsync(string rawRefreshToken, CancellationToken cancellationToken);
    Task LogoutAsync(string? rawRefreshToken, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> ResendEmailAsync(ResendEmailRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<CurrentAccountResponse>> GetCurrentAsync(CancellationToken cancellationToken);
}
