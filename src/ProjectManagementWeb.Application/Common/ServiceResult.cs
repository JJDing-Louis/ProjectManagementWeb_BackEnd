namespace ProjectManagementWeb.Application.Common;

public sealed class ServiceResult<T>
{
    private ServiceResult(T? value, ServiceError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public ServiceError? Error { get; }
    public bool IsSuccess => Error is null;

    public static ServiceResult<T> Success(T value) => new(value, null);
    public static ServiceResult<T> Failure(
        string code,
        string message,
        int statusCode,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        new(default, new ServiceError(code, message, statusCode, fieldErrors));
}
