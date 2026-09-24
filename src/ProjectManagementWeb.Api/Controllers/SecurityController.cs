using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ProjectManagementWeb.Api.Controllers;

[Route("api/v1/security")]
public sealed class SecurityController : ApiControllerBase
{
    [AllowAnonymous]
    [HttpGet("csrf-token")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetCsrfToken([FromServices] IAntiforgery antiforgery)
    {
        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { token = tokens.RequestToken });
    }
}
