using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.Application.Projects;

public interface IProjectService
{
    Task<ServiceResult<PagedResult<ProjectResponse>>> GetProjectsAsync(ProjectQuery query, CancellationToken cancellationToken);
    Task<ServiceResult<ProjectResponse>> GetProjectAsync(Guid id, CancellationToken cancellationToken);
    Task<ServiceResult<ProjectResponse>> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<ProjectResponse>> UpdateProjectAsync(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<IReadOnlyCollection<ProjectMemberResponse>>> GetMembersAsync(Guid projectId, CancellationToken cancellationToken);
    Task<ServiceResult<PagedResult<MemberCandidateResponse>>> GetMemberCandidatesAsync(Guid projectId, MemberCandidateQuery query, CancellationToken cancellationToken);
    Task<ServiceResult<ProjectMemberResponse>> AddMemberAsync(Guid projectId, SaveProjectMemberRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<ProjectMemberResponse>> UpdateMemberAsync(Guid projectId, Guid accountId, UpdateProjectMemberRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> RemoveMemberAsync(Guid projectId, Guid accountId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ProjectRoleResponse>> GetProjectRolesAsync(CancellationToken cancellationToken);
}
