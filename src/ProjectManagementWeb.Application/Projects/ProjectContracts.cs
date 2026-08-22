using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Domain.Enums;

namespace ProjectManagementWeb.Application.Projects;

public sealed record ProjectQuery(string? Search, ProjectStatus? Status, int Page = 1, int PageSize = 20);
public sealed record CreateProjectRequest(string Code, string Name, string? Description, Guid OwnerAccountId);
public sealed record UpdateProjectRequest(string Name, string? Description, Guid OwnerAccountId, ProjectStatus Status, string RowVersion);
public sealed record ProjectResponse(Guid Id, string Code, string Name, string? Description, Guid OwnerAccountId, ProjectStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string RowVersion);
public sealed record SaveProjectMemberRequest(Guid AccountId, IReadOnlyCollection<Guid> ProjectRoleIds);
public sealed record UpdateProjectMemberRequest(IReadOnlyCollection<Guid> ProjectRoleIds);
public sealed record ProjectMemberResponse(Guid AccountId, string Account, string? Name, IReadOnlyCollection<ProjectRoleResponse> Roles);
public sealed record ProjectRoleResponse(Guid Id, string Code, string Name);
public sealed record ProjectListResponse(PagedResult<ProjectResponse> Result);
