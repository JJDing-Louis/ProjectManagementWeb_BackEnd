using System.Data;
using Microsoft.EntityFrameworkCore;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Application.Projects;
using ProjectManagementWeb.Application.Users;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Domain.Enums;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.Infrastructure.Services;

internal sealed class ProjectService : IProjectService
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ServiceSupport _support;
    private readonly TimeProvider _timeProvider;
    private readonly IBusinessCodeGenerator _codeGenerator;
    private readonly BootstrapAdminPolicy _bootstrapAdmin;

    public ProjectService(ApplicationDbContext db, ICurrentUser currentUser, ServiceSupport support,
        TimeProvider timeProvider, IBusinessCodeGenerator codeGenerator, BootstrapAdminPolicy bootstrapAdmin)
    {
        _db = db;
        _currentUser = currentUser;
        _support = support;
        _timeProvider = timeProvider;
        _codeGenerator = codeGenerator;
        _bootstrapAdmin = bootstrapAdmin;
    }

    public async Task<ServiceResult<PagedResult<ProjectResponse>>> GetProjectsAsync(ProjectQuery query, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.ProjectsRead) || _currentUser.AccountId is not Guid accountId)
        {
            return ServiceResult<PagedResult<ProjectResponse>>.Failure("forbidden", "沒有讀取專案的權限。", 403);
        }
        int page = Math.Max(query.Page, 1);
        int pageSize = Math.Clamp(query.PageSize, 1, 100);
        IQueryable<Project> projects = _db.Projects.AsNoTracking();
        if (!_currentUser.HasFunction(SystemFunctions.ProjectsManageAll))
        {
            projects = from project in projects
                       join member in _db.ProjectMembers on project.Id equals member.ProjectId
                       where member.AccountId == accountId
                       select project;
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string search = query.Search.Trim();
            projects = projects.Where(x => x.Code.Contains(search) || x.Name.Contains(search) ||
                                            (x.Description != null && x.Description.Contains(search)));
        }
        if (query.Status is not null)
        {
            projects = projects.Where(x => x.Status == query.Status.Value);
        }
        int total = await projects.CountAsync(cancellationToken);
        Project[] rows = await projects.OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);
        ProjectResponse[] items = rows.Select(Map).ToArray();
        return ServiceResult<PagedResult<ProjectResponse>>.Success(new PagedResult<ProjectResponse>(items, page, pageSize, total));
    }

    public async Task<ServiceResult<ProjectResponse>> GetProjectAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await _support.CanReadProjectAsync(id, cancellationToken))
        {
            return ServiceResult<ProjectResponse>.Failure("forbidden", "沒有讀取此專案的權限。", 403);
        }
        Project? project = await _db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return project is null
            ? ServiceResult<ProjectResponse>.Failure("not_found", "找不到專案。", 404)
            : ServiceResult<ProjectResponse>.Success(Map(project));
    }

    public async Task<ServiceResult<ProjectResponse>> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.ProjectsCreate) || _currentUser.AccountId is not Guid actorId)
        {
            return ServiceResult<ProjectResponse>.Failure("forbidden", "沒有建立專案的權限。", 403);
        }
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ServiceResult<ProjectResponse>.Failure("validation_error", "專案名稱為必填。", 400);
        }
        ApplicationUser? owner = await _db.Users.SingleOrDefaultAsync(x => x.Id == request.OwnerAccountId && x.IsEnabled, cancellationToken);
        if (owner is null)
        {
            return ServiceResult<ProjectResponse>.Failure("invalid_owner", "Owner 必須是有效帳號。", 422);
        }
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        string? code = await _codeGenerator.GenerateAsync(BusinessCodeType.Project, now, cancellationToken);
        if (code is null)
        {
            return ServiceResult<ProjectResponse>.Failure("daily_code_limit_exceeded", "今日 Project 編號已達上限。", 409);
        }
        var project = new Project(Guid.NewGuid(), code, request.Name.Trim(), request.Description?.Trim(), request.OwnerAccountId, now);
        _db.Projects.Add(project);
        _db.ProjectMembers.Add(new ProjectMember(project.Id, request.OwnerAccountId, now));
        _db.ProjectMemberRoles.Add(new ProjectMemberRole(project.Id, request.OwnerAccountId,
            SeedIds.Create($"project-role:{ProjectRoleCodes.ProjectManager}")));
        _support.AddAudit("Create", "Project", project.Id.ToString(), null, new { project.Code, project.Name }, now);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ServiceResult<ProjectResponse>.Failure("duplicate_project_code", "專案編號已存在。", 409);
        }
        return ServiceResult<ProjectResponse>.Success(Map(project));
    }

    public async Task<ServiceResult<ProjectResponse>> UpdateProjectAsync(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        if (!await _support.CanManageProjectAsync(id, cancellationToken))
        {
            return ServiceResult<ProjectResponse>.Failure("forbidden", "沒有修改此專案的權限。", 403);
        }
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        Project? project = await _db.Projects.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (project is null)
        {
            return ServiceResult<ProjectResponse>.Failure("not_found", "找不到專案。", 404);
        }
        if (!ServiceSupport.TryDecodeRowVersion(request.RowVersion, out byte[] version) || !project.RowVersion.SequenceEqual(version))
        {
            return ServiceResult<ProjectResponse>.Failure("concurrency_conflict", "專案已被其他使用者更新，請重新載入。", 409);
        }
        bool ownerIsMember = await _db.ProjectMembers.AnyAsync(x => x.ProjectId == id && x.AccountId == request.OwnerAccountId, cancellationToken);
        if (!ownerIsMember)
        {
            return ServiceResult<ProjectResponse>.Failure("invalid_owner", "Owner 必須是專案成員。", 422);
        }
        Guid managerRoleId = SeedIds.Create($"project-role:{ProjectRoleCodes.ProjectManager}");
        bool ownerIsManager = await _db.ProjectMemberRoles.AnyAsync(x => x.ProjectId == id &&
            x.AccountId == request.OwnerAccountId && x.ProjectRoleId == managerRoleId, cancellationToken);
        if (!ownerIsManager)
        {
            _db.ProjectMemberRoles.Add(new ProjectMemberRole(id, request.OwnerAccountId, managerRoleId));
        }
        var before = new { project.Name, project.Description, project.OwnerAccountId, project.Status, project.VersionNumber };
        project.Update(request.Name.Trim(), request.Description?.Trim(), request.OwnerAccountId, request.Status, _timeProvider.GetUtcNow());
        _support.AddAudit("Update", "Project", id.ToString(), before,
            new { project.Name, project.Description, project.OwnerAccountId, project.Status, project.VersionNumber }, _timeProvider.GetUtcNow());
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult<ProjectResponse>.Failure("concurrency_conflict", "專案已被其他使用者更新，請重新載入。", 409);
        }
        return ServiceResult<ProjectResponse>.Success(Map(project));
    }

    public async Task<ServiceResult<IReadOnlyCollection<ProjectMemberResponse>>> GetMembersAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (!await _support.CanReadProjectAsync(projectId, cancellationToken))
        {
            return ServiceResult<IReadOnlyCollection<ProjectMemberResponse>>.Failure("forbidden", "沒有讀取成員的權限。", 403);
        }
        return ServiceResult<IReadOnlyCollection<ProjectMemberResponse>>.Success(await MapMembersAsync(projectId, null, cancellationToken));
    }

    public async Task<ServiceResult<PagedResult<MemberCandidateResponse>>> GetMemberCandidatesAsync(Guid projectId,
        MemberCandidateQuery query, CancellationToken cancellationToken)
    {
        if (!await _support.CanManageProjectAsync(projectId, cancellationToken))
        {
            return ServiceResult<PagedResult<MemberCandidateResponse>>.Failure("forbidden", "沒有管理成員的權限。", 403);
        }

        int page = Math.Max(query.Page, 1);
        int pageSize = Math.Clamp(query.PageSize, 1, 100);
        IQueryable<ApplicationUser> candidates = _db.Users.AsNoTracking().Where(user => user.IsEnabled &&
            !_db.ProjectMembers.Any(member => member.ProjectId == projectId && member.AccountId == user.Id));
        if (_bootstrapAdmin.NormalizedAccount is string normalizedBootstrapAdmin)
        {
            candidates = candidates.Where(user => user.NormalizedUserName != normalizedBootstrapAdmin);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string search = query.Search.Trim();
            candidates = candidates.Where(x => (x.UserName != null && x.UserName.Contains(search)) ||
                                                 (x.Name != null && x.Name.Contains(search)));
        }

        int total = await candidates.CountAsync(cancellationToken);
        MemberCandidateResponse[] items = await candidates.OrderBy(x => x.UserName)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new MemberCandidateResponse(x.Id, x.UserName ?? string.Empty, x.Name))
            .ToArrayAsync(cancellationToken);
        return ServiceResult<PagedResult<MemberCandidateResponse>>.Success(
            new PagedResult<MemberCandidateResponse>(items, page, pageSize, total));
    }

    public async Task<ServiceResult<ProjectMemberResponse>> AddMemberAsync(Guid projectId, SaveProjectMemberRequest request, CancellationToken cancellationToken)
    {
        if (!await _support.CanManageProjectAsync(projectId, cancellationToken))
        {
            return ServiceResult<ProjectMemberResponse>.Failure("forbidden", "沒有管理成員的權限。", 403);
        }
        if (request.ProjectRoleIds.Count == 0 || !await RolesExistAsync(request.ProjectRoleIds, cancellationToken))
        {
            return ServiceResult<ProjectMemberResponse>.Failure("invalid_project_roles", "至少指定一個有效的專案角色。", 422);
        }
        ApplicationUser? account = await _db.Users.SingleOrDefaultAsync(
            x => x.Id == request.AccountId && x.IsEnabled, cancellationToken);
        if (account is null)
        {
            return ServiceResult<ProjectMemberResponse>.Failure("invalid_account", "帳號不存在或已停用。", 422);
        }
        if (_bootstrapAdmin.IsBootstrapAdmin(account.UserName))
        {
            return ServiceResult<ProjectMemberResponse>.Failure(
                "bootstrap_admin_not_project_member", "系統預設 Admin 不可加入專案成員。", 422);
        }
        if (await _db.ProjectMembers.AnyAsync(x => x.ProjectId == projectId && x.AccountId == request.AccountId, cancellationToken))
        {
            return ServiceResult<ProjectMemberResponse>.Failure("duplicate_member", "此帳號已是專案成員。", 409);
        }
        DateTimeOffset now = _timeProvider.GetUtcNow();
        _db.ProjectMembers.Add(new ProjectMember(projectId, request.AccountId, now));
        _db.ProjectMemberRoles.AddRange(request.ProjectRoleIds.Distinct().Select(roleId => new ProjectMemberRole(projectId, request.AccountId, roleId)));
        _support.AddAudit("AddMember", "Project", projectId.ToString(), null, new { request.AccountId, request.ProjectRoleIds }, now);
        await _db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ProjectMemberResponse>.Success((await MapMembersAsync(projectId, request.AccountId, cancellationToken)).Single());
    }

    public async Task<ServiceResult<ProjectMemberResponse>> UpdateMemberAsync(Guid projectId, Guid accountId,
        UpdateProjectMemberRequest request, CancellationToken cancellationToken)
    {
        if (!await _support.CanManageProjectAsync(projectId, cancellationToken))
        {
            return ServiceResult<ProjectMemberResponse>.Failure("forbidden", "沒有管理成員的權限。", 403);
        }
        if (request.ProjectRoleIds.Count == 0 || !await RolesExistAsync(request.ProjectRoleIds, cancellationToken))
        {
            return ServiceResult<ProjectMemberResponse>.Failure("invalid_project_roles", "至少指定一個有效的專案角色。", 422);
        }
        bool exists = await _db.ProjectMembers.AnyAsync(x => x.ProjectId == projectId && x.AccountId == accountId, cancellationToken);
        if (!exists)
        {
            return ServiceResult<ProjectMemberResponse>.Failure("not_found", "找不到專案成員。", 404);
        }
        List<ProjectMemberRole> current = await _db.ProjectMemberRoles.Where(x => x.ProjectId == projectId && x.AccountId == accountId).ToListAsync(cancellationToken);
        var before = current.Select(x => x.ProjectRoleId).ToArray();
        _db.ProjectMemberRoles.RemoveRange(current);
        _db.ProjectMemberRoles.AddRange(request.ProjectRoleIds.Distinct().Select(roleId => new ProjectMemberRole(projectId, accountId, roleId)));
        _support.AddAudit("UpdateMemberRoles", "Project", projectId.ToString(), new { accountId, Roles = before },
            new { accountId, Roles = request.ProjectRoleIds }, _timeProvider.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ProjectMemberResponse>.Success((await MapMembersAsync(projectId, accountId, cancellationToken)).Single());
    }

    public async Task<ServiceResult<bool>> RemoveMemberAsync(Guid projectId, Guid accountId, CancellationToken cancellationToken)
    {
        if (!await _support.CanManageProjectAsync(projectId, cancellationToken))
        {
            return ServiceResult<bool>.Failure("forbidden", "沒有管理成員的權限。", 403);
        }
        Project? project = await _db.Projects.SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        ProjectMember? member = await _db.ProjectMembers.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.AccountId == accountId, cancellationToken);
        if (project is null || member is null)
        {
            return ServiceResult<bool>.Failure("not_found", "找不到專案成員。", 404);
        }
        if (project.OwnerAccountId == accountId)
        {
            return ServiceResult<bool>.Failure("owner_transfer_required", "請先移交 Project Owner。", 409);
        }
        bool hasOpenTasks = await _db.TaskItems.AnyAsync(x => x.ProjectId == projectId && x.AssignedAccountId == accountId &&
            x.Status != ProjectManagementWeb.Domain.Enums.TaskStatus.Completed, cancellationToken);
        if (hasOpenTasks)
        {
            return ServiceResult<bool>.Failure("task_reassignment_required", "此成員仍有未完成 Task，請先重新指派。", 409);
        }
        _db.ProjectMembers.Remove(member);
        _support.AddAudit("RemoveMember", "Project", projectId.ToString(), new { accountId }, null, _timeProvider.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<IReadOnlyCollection<ProjectRoleResponse>> GetProjectRolesAsync(CancellationToken cancellationToken) =>
        await _db.ProjectRoles.AsNoTracking().OrderBy(x => x.Code)
            .Select(x => new ProjectRoleResponse(x.Id, x.Code, x.Name)).ToArrayAsync(cancellationToken);

    private async Task<bool> RolesExistAsync(IEnumerable<Guid> roleIds, CancellationToken cancellationToken)
    {
        Guid[] distinct = roleIds.Distinct().ToArray();
        return distinct.Length > 0 && await _db.ProjectRoles.CountAsync(x => distinct.Contains(x.Id), cancellationToken) == distinct.Length;
    }

    private async Task<IReadOnlyCollection<ProjectMemberResponse>> MapMembersAsync(Guid projectId, Guid? accountId, CancellationToken cancellationToken)
    {
        IQueryable<ProjectMember> members = _db.ProjectMembers.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (accountId is not null)
        {
            members = members.Where(x => x.AccountId == accountId);
        }
        var baseRows = await (from member in members
                              join account in _db.Users.AsNoTracking() on member.AccountId equals account.Id
                              select new { member.AccountId, Account = account.UserName ?? string.Empty, account.Name }).ToListAsync(cancellationToken);
        var responses = new List<ProjectMemberResponse>(baseRows.Count);
        foreach (var row in baseRows)
        {
            ProjectRoleResponse[] roles = await (from memberRole in _db.ProjectMemberRoles.AsNoTracking()
                                                 join role in _db.ProjectRoles.AsNoTracking() on memberRole.ProjectRoleId equals role.Id
                                                 where memberRole.ProjectId == projectId && memberRole.AccountId == row.AccountId
                                                 orderby role.Code
                                                 select new ProjectRoleResponse(role.Id, role.Code, role.Name)).ToArrayAsync(cancellationToken);
            responses.Add(new ProjectMemberResponse(row.AccountId, row.Account, row.Name, roles));
        }
        return responses;
    }

    private static ProjectResponse Map(Project project) => new(project.Id, project.Code, project.Name, project.Description,
        project.OwnerAccountId, project.Status, project.CreatedAt, project.UpdatedAt, project.VersionNumber,
        Convert.ToBase64String(project.RowVersion));
}
