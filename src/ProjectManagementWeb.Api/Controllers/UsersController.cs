using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Application.Preferences;
using ProjectManagementWeb.Application.Users;

namespace ProjectManagementWeb.Api.Controllers;

[Authorize]
[Route("api/v1/users")]
public sealed class UsersController : ApiControllerBase
{
    private readonly IUserService _users;
    private readonly IPreferenceService _preferences;

    public UsersController(IUserService users, IPreferenceService preferences)
    {
        _users = users;
        _preferences = preferences;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<UserResponse>>> GetUsers([FromQuery] UserQuery query, CancellationToken cancellationToken) =>
        FromResult(await _users.GetUsersAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserResponse>> GetUser(Guid id, CancellationToken cancellationToken) =>
        FromResult(await _users.GetUserAsync(id, cancellationToken));

    [HttpPut("{id:guid}/role")]
    public async Task<ActionResult<UserResponse>> ReplaceRole(Guid id, UpdateRoleRequest request, CancellationToken cancellationToken) =>
        FromResult(await _users.ReplaceRoleAsync(id, request, cancellationToken));

    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<UserResponse>> UpdateStatus(Guid id, UpdateUserStatusRequest request, CancellationToken cancellationToken) =>
        FromResult(await _users.UpdateStatusAsync(id, request, cancellationToken));

    [HttpPut("{id:guid}/administration")]
    public async Task<ActionResult<UserResponse>> UpdateAdministration(Guid id, UpdateAdministrationRequest request,
        CancellationToken cancellationToken) =>
        FromResult(await _users.UpdateAdministrationAsync(id, request, cancellationToken));

    [HttpGet("me/preferences")]
    public async Task<ActionResult<PreferenceResponse>> GetPreferences(CancellationToken cancellationToken) =>
        FromResult(await _preferences.GetAsync(cancellationToken));

    [HttpPut("me/preferences")]
    public async Task<ActionResult<PreferenceResponse>> UpdatePreferences(UpdatePreferenceRequest request, CancellationToken cancellationToken) =>
        FromResult(await _preferences.UpdateAsync(request, cancellationToken));
}
