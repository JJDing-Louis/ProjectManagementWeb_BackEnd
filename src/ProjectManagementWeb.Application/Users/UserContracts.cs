using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.Application.Users;

public sealed record UserQuery(string? Search, string? Role, int Page = 1, int PageSize = 20);
public sealed record UserResponse(Guid Id, string Account, string Email, string? Name, bool EmailConfirmed, bool IsEnabled, string Role);
public sealed record UpdateRoleRequest(Guid RoleId);
public sealed record UpdateUserStatusRequest(bool IsEnabled);
public sealed record RoleResponse(Guid Id, string Name, IReadOnlyCollection<string> Functions);
public sealed record UserListResponse(PagedResult<UserResponse> Result);
