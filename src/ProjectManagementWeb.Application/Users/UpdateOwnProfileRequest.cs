namespace ProjectManagementWeb.Application.Users;

/// <summary>更新目前登入使用者名稱與電話號碼的請求。</summary>
public sealed record UpdateOwnProfileRequest(string Name, string? PhoneNumber);
