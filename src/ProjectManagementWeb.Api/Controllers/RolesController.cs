using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectManagementWeb.Application.Users;

namespace ProjectManagementWeb.Api.Controllers;

[Authorize]
[Route("api/v1/roles")]
public sealed class RolesController : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<RoleResponse>>> GetRoles(
        [FromServices] IUserService service, CancellationToken cancellationToken) =>
        Ok(await service.GetRolesAsync(cancellationToken));
}
