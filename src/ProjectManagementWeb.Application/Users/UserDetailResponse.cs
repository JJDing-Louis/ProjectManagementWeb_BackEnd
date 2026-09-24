namespace ProjectManagementWeb.Application.Users;

/// <summary>使用者詳情回應，包含只在單筆詳情查詢提供的個人資料。</summary>
public sealed record UserDetailResponse(
    Guid Id,
    string Account,
    string Email,
    string? Name,
    string? PhoneNumber,
    bool EmailConfirmed,
    bool IsEnabled,
    string Role,
    bool IsBootstrapAdmin);
