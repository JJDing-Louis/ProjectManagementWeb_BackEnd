using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ProjectManagementWeb.Api;

internal sealed class ApiExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ApiExceptionHandler> _logger;

    public ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "處理 API 請求時發生未預期錯誤。");
        var details = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "伺服器發生未預期錯誤。",
            Type = "https://www.rfc-editor.org/rfc/rfc9110#section-15.6.1",
            Instance = context.Request.Path
        };
        details.Extensions["traceId"] = context.TraceIdentifier;
        context.Response.StatusCode = details.Status.Value;
        await context.Response.WriteAsJsonAsync(details, cancellationToken);
        return true;
    }
}
