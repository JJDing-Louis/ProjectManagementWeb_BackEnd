using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Application.Users;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.Infrastructure.Services;

internal sealed class UserService : IUserService
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly ICurrentUser _currentUser;
    private readonly ServiceSupport _support;
    private readonly TimeProvider _timeProvider;

    public UserService(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager, ICurrentUser currentUser, ServiceSupport support, TimeProvider timeProvider)
    {
        _db = db;
        _userManager = userManager;
        _roleManager = roleManager;
        _currentUser = currentUser;
        _support = support;
        _timeProvider = timeProvider;
    }

    public async Task<ServiceResult<PagedResult<UserResponse>>> GetUsersAsync(UserQuery query, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.AccountsRead))
        {
            return ServiceResult<PagedResult<UserResponse>>.Failure("forbidden", "沒有查詢帳號的權限。", 403);
        }
        int page = Math.Max(query.Page, 1);
        int pageSize = Math.Clamp(query.PageSize, 1, 100);
        IQueryable<ApplicationUser> users = _db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string search = query.Search.Trim();
            users = users.Where(x => (x.UserName != null && x.UserName.Contains(search)) ||
                                     (x.Email != null && x.Email.Contains(search)) ||
                                     (x.Name != null && x.Name.Contains(search)));
        }
        if (!string.IsNullOrWhiteSpace(query.Role))
        {
            Guid roleId = SeedIds.Create($"role:{query.Role}");
            users = from user in users
                    join userRole in _db.Set<ApplicationUserRole>() on user.Id equals userRole.UserId
                    where userRole.RoleId == roleId
                    select user;
        }
        int total = await users.CountAsync(cancellationToken);
        List<ApplicationUser> pageUsers = await users.OrderBy(x => x.UserName)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var responses = new List<UserResponse>(pageUsers.Count);
        foreach (ApplicationUser user in pageUsers)
        {
            responses.Add(await MapAsync(user));
        }
        return ServiceResult<PagedResult<UserResponse>>.Success(new PagedResult<UserResponse>(responses, page, pageSize, total));
    }

    public async Task<ServiceResult<UserResponse>> GetUserAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.AccountsRead) && _currentUser.AccountId != id)
        {
            return ServiceResult<UserResponse>.Failure("forbidden", "沒有查詢帳號的權限。", 403);
        }
        ApplicationUser? user = await _userManager.FindByIdAsync(id.ToString());
        return user is null
            ? ServiceResult<UserResponse>.Failure("not_found", "找不到帳號。", 404)
            : ServiceResult<UserResponse>.Success(await MapAsync(user));
    }

    public async Task<ServiceResult<UserResponse>> ReplaceRoleAsync(Guid id, UpdateRoleRequest request, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.AccountsManageRole))
        {
            return ServiceResult<UserResponse>.Failure("forbidden", "只有 Admin 可以調整系統角色。", 403);
        }
        ApplicationUser? user = await _userManager.FindByIdAsync(id.ToString());
        ApplicationRole? role = await _roleManager.FindByIdAsync(request.RoleId.ToString());
        if (user is null || role?.Name is null)
        {
            return ServiceResult<UserResponse>.Failure("not_found", "找不到帳號或角色。", 404);
        }
        if (!user.EmailConfirmed && role.Name != SystemRoles.Viewer)
        {
            return ServiceResult<UserResponse>.Failure("email_not_confirmed", "Email 尚未驗證，只能使用 Viewer。", 422);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        IList<string> currentRoles = await _userManager.GetRolesAsync(user);
        string currentRole = currentRoles.SingleOrDefault() ?? SystemRoles.Viewer;
        if (currentRole == SystemRoles.Admin && role.Name != SystemRoles.Admin && await CountAdminsAsync(cancellationToken) <= 1)
        {
            return ServiceResult<UserResponse>.Failure("last_admin", "不可移除最後一位 Admin。", 409);
        }
        if (currentRoles.Count > 0)
        {
            IdentityResult removed = await _userManager.RemoveFromRolesAsync(user, currentRoles);
            if (!removed.Succeeded)
            {
                return ServiceResult<UserResponse>.Failure("role_update_failed", "無法移除原角色。", 500);
            }
        }
        IdentityResult added = await _userManager.AddToRoleAsync(user, role.Name);
        if (!added.Succeeded)
        {
            return ServiceResult<UserResponse>.Failure("role_update_failed", "無法設定角色。", 500);
        }
        user.TokenVersion++;
        await _userManager.UpdateAsync(user);
        await RevokeTokensAsync(user.Id, cancellationToken);
        _support.AddAudit("ReplaceRole", "Account", user.Id.ToString(), new { Role = currentRole }, new { Role = role.Name }, _timeProvider.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<UserResponse>.Success(await MapAsync(user));
    }

    public async Task<ServiceResult<UserResponse>> UpdateStatusAsync(Guid id, UpdateUserStatusRequest request, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.AccountsManageStatus))
        {
            return ServiceResult<UserResponse>.Failure("forbidden", "只有 Admin 可以調整帳號狀態。", 403);
        }
        ApplicationUser? user = await _userManager.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return ServiceResult<UserResponse>.Failure("not_found", "找不到帳號。", 404);
        }
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        IList<string> roles = await _userManager.GetRolesAsync(user);
        if (!request.IsEnabled && roles.SingleOrDefault() == SystemRoles.Admin && await CountAdminsAsync(cancellationToken) <= 1)
        {
            return ServiceResult<UserResponse>.Failure("last_admin", "不可停用最後一位 Admin。", 409);
        }
        bool before = user.IsEnabled;
        user.IsEnabled = request.IsEnabled;
        user.TokenVersion++;
        await _userManager.UpdateAsync(user);
        await RevokeTokensAsync(user.Id, cancellationToken);
        _support.AddAudit("UpdateStatus", "Account", user.Id.ToString(), new { IsEnabled = before }, new { request.IsEnabled }, _timeProvider.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<UserResponse>.Success(await MapAsync(user));
    }

    public async Task<ServiceResult<UserResponse>> UpdateAdministrationAsync(Guid id,
        UpdateAdministrationRequest request, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.AccountsManageRole) ||
            !_currentUser.HasFunction(SystemFunctions.AccountsManageStatus))
        {
            return ServiceResult<UserResponse>.Failure("forbidden", "只有 Admin 可以管理帳號。", 403);
        }

        ApplicationUser? user = await _userManager.FindByIdAsync(id.ToString());
        ApplicationRole? role = await _roleManager.FindByIdAsync(request.RoleId.ToString());
        if (user is null || role?.Name is null)
        {
            return ServiceResult<UserResponse>.Failure("not_found", "找不到帳號或角色。", 404);
        }
        if (!user.EmailConfirmed && role.Name != SystemRoles.Viewer)
        {
            return ServiceResult<UserResponse>.Failure("email_not_confirmed", "Email 尚未驗證，只能使用 Viewer。", 422);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        IList<string> currentRoles = await _userManager.GetRolesAsync(user);
        string currentRole = currentRoles.SingleOrDefault() ?? SystemRoles.Viewer;
        bool removesEnabledAdmin = currentRole == SystemRoles.Admin && user.IsEnabled &&
                                   (role.Name != SystemRoles.Admin || !request.IsEnabled);
        if (removesEnabledAdmin && await CountAdminsAsync(cancellationToken) <= 1)
        {
            return ServiceResult<UserResponse>.Failure("last_admin", "不可停用或移除最後一位 Admin。", 409);
        }

        if (!string.Equals(currentRole, role.Name, StringComparison.Ordinal))
        {
            if (currentRoles.Count > 0)
            {
                IdentityResult removed = await _userManager.RemoveFromRolesAsync(user, currentRoles);
                if (!removed.Succeeded)
                {
                    return ServiceResult<UserResponse>.Failure("role_update_failed", "無法移除原角色。", 500);
                }
            }
            IdentityResult added = await _userManager.AddToRoleAsync(user, role.Name);
            if (!added.Succeeded)
            {
                return ServiceResult<UserResponse>.Failure("role_update_failed", "無法設定角色。", 500);
            }
        }

        bool wasEnabled = user.IsEnabled;
        user.IsEnabled = request.IsEnabled;
        user.TokenVersion++;
        IdentityResult updated = await _userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return ServiceResult<UserResponse>.Failure("account_update_failed", "無法更新帳號。", 500);
        }
        await RevokeTokensAsync(user.Id, cancellationToken);
        _support.AddAudit("UpdateAdministration", "Account", user.Id.ToString(),
            new { Role = currentRole, IsEnabled = wasEnabled },
            new { Role = role.Name, request.IsEnabled }, _timeProvider.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<UserResponse>.Success(await MapAsync(user));
    }

    public async Task<IReadOnlyCollection<RoleResponse>> GetRolesAsync(CancellationToken cancellationToken)
    {
        List<ApplicationRole> roles = await _db.Roles.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var responses = new List<RoleResponse>(roles.Count);
        foreach (ApplicationRole role in roles)
        {
            string[] functions = await (from mapping in _db.RoleFunctions
                                        join function in _db.Functions on mapping.FunctionId equals function.Id
                                        where mapping.RoleId == role.Id
                                        orderby function.Code
                                        select function.Code).ToArrayAsync(cancellationToken);
            responses.Add(new RoleResponse(role.Id, role.Name ?? string.Empty, functions));
        }
        return responses;
    }

    private async Task<UserResponse> MapAsync(ApplicationUser user)
    {
        IList<string> roles = await _userManager.GetRolesAsync(user);
        return new UserResponse(user.Id, user.UserName ?? string.Empty, user.Email ?? string.Empty,
            user.Name, user.EmailConfirmed, user.IsEnabled, roles.SingleOrDefault() ?? SystemRoles.Viewer);
    }

    private async Task<int> CountAdminsAsync(CancellationToken cancellationToken)
    {
        Guid adminRoleId = SeedIds.Create($"role:{SystemRoles.Admin}");
        return await (from userRole in _db.Set<ApplicationUserRole>()
                      join user in _db.Users on userRole.UserId equals user.Id
                      where userRole.RoleId == adminRoleId && user.IsEnabled
                      select userRole.UserId).CountAsync(cancellationToken);
    }

    private async Task RevokeTokensAsync(Guid accountId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<ProjectManagementWeb.Domain.Entities.RefreshToken> tokens = await _db.RefreshTokens
            .Where(x => x.AccountId == accountId && x.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (ProjectManagementWeb.Domain.Entities.RefreshToken token in tokens)
        {
            token.Revoke(now);
        }
    }
}
