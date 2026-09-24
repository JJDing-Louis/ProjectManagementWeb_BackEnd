namespace ProjectManagementWeb.Application.Common;

public interface ICurrentUser
{
    Guid? AccountId { get; }
    string? Role { get; }
    bool IsAuthenticated { get; }
    bool HasFunction(string functionCode);
}
