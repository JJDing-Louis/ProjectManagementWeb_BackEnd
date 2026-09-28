namespace ProjectManagementWeb.Application.Users;

/// <summary>已處理的大頭貼圖片內容。</summary>
public sealed record AvatarImageResponse(byte[] Content, string ContentType);
