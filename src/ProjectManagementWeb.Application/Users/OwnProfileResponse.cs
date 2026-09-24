namespace ProjectManagementWeb.Application.Users;

/// <summary>目前登入使用者可查看與維護的個人資料。</summary>
public sealed record OwnProfileResponse(string Name, string? PhoneNumber);
