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

        return FromError(result.Error!);
    }

    protected ObjectResult FromError(ServiceError error)
    {
        ProblemDetails details = error.FieldErrors is { Count: > 0 }
            ? new ValidationProblemDetails(error.FieldErrors.ToDictionary(pair => pair.Key, pair => pair.Value))
            : new ProblemDetails();
        details.Status = error.StatusCode;
        details.Title = error.Message;
        details.Type = $"https://httpstatuses.com/{error.StatusCode}";
        details.Instance = HttpContext.Request.Path;
        details.Extensions["code"] = error.Code;
        details.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return new ObjectResult(details) { StatusCode = error.StatusCode };
    }
}
