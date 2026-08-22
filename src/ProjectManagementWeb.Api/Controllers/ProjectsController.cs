using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Application.Projects;

namespace ProjectManagementWeb.Api.Controllers;

[Authorize]
[Route("api/v1/projects")]
public sealed class ProjectsController : ApiControllerBase
{
    private readonly IProjectService _projects;

    public ProjectsController(IProjectService projects) => _projects = projects;

    [HttpGet]
    public async Task<ActionResult<PagedResult<ProjectResponse>>> GetProjects([FromQuery] ProjectQuery query, CancellationToken cancellationToken) =>
        FromResult(await _projects.GetProjectsAsync(query, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<ProjectResponse>> CreateProject(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var result = await _projects.CreateProjectAsync(request, cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetProject), new { id = result.Value!.Id }, result.Value)
            : FromResult(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProjectResponse>> GetProject(Guid id, CancellationToken cancellationToken) =>
        FromResult(await _projects.GetProjectAsync(id, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProjectResponse>> UpdateProject(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken) =>
        FromResult(await _projects.UpdateProjectAsync(id, request, cancellationToken));

    [HttpGet("roles")]
    public async Task<ActionResult<IReadOnlyCollection<ProjectRoleResponse>>> GetProjectRoles(CancellationToken cancellationToken) =>
        Ok(await _projects.GetProjectRolesAsync(cancellationToken));

    [HttpGet("{id:guid}/members")]
    public async Task<ActionResult<IReadOnlyCollection<ProjectMemberResponse>>> GetMembers(Guid id, CancellationToken cancellationToken) =>
        FromResult(await _projects.GetMembersAsync(id, cancellationToken));

    [HttpPost("{id:guid}/members")]
    public async Task<ActionResult<ProjectMemberResponse>> AddMember(Guid id, SaveProjectMemberRequest request, CancellationToken cancellationToken) =>
        FromResult(await _projects.AddMemberAsync(id, request, cancellationToken));

    [HttpPut("{id:guid}/members/{accountId:guid}")]
    public async Task<ActionResult<ProjectMemberResponse>> UpdateMember(Guid id, Guid accountId,
        UpdateProjectMemberRequest request, CancellationToken cancellationToken) =>
        FromResult(await _projects.UpdateMemberAsync(id, accountId, request, cancellationToken));

    [HttpDelete("{id:guid}/members/{accountId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid accountId, CancellationToken cancellationToken)
    {
        var result = await _projects.RemoveMemberAsync(id, accountId, cancellationToken);
        return result.IsSuccess ? NoContent() : FromResult(result).Result!;
    }
}
