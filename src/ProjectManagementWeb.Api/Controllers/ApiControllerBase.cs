using Microsoft.AspNetCore.Mvc;
using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.Api.Controllers;

[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    protected ActionResult<T> FromResult<T>(ServiceResult<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        ServiceError error = result.Error!;
        var details = new ProblemDetails
        {
            Status = error.StatusCode,
            Title = error.Message,
            Type = $"https://httpstatuses.com/{error.StatusCode}",
            Instance = HttpContext.Request.Path
        };
        details.Extensions["code"] = error.Code;
        details.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return new ObjectResult(details) { StatusCode = error.StatusCode };
    }
}
