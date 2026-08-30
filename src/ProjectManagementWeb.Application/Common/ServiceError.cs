namespace ProjectManagementWeb.Application.Common;

public sealed record ServiceError(
    string Code,
    string Message,
    int StatusCode,
    IReadOnlyDictionary<string, string[]>? FieldErrors = null);
