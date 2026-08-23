using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.Application.Users;

public interface IUserService
{
    Task<ServiceResult<PagedResult<UserResponse>>> GetUsersAsync(UserQuery query, CancellationToken cancellationToken);
    Task<ServiceResult<UserResponse>> GetUserAsync(Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<UserResponse>> ReplaceRoleAsync(Guid id, UpdateRoleRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<UserResponse>> UpdateStatusAsync(Guid id, UpdateUserStatusRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<UserResponse>> UpdateAdministrationAsync(Guid id, UpdateAdministrationRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<RoleResponse>> GetRolesAsync(CancellationToken cancellationToken);
}
