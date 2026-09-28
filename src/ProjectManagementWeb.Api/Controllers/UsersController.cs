using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Application.Preferences;
using ProjectManagementWeb.Application.Users;

namespace ProjectManagementWeb.Api.Controllers;

[Authorize]
[Route("api/v1/users")]
public sealed class UsersController : ApiControllerBase
{
    private readonly IUserService _users;
    private readonly IPreferenceService _preferences;
    private readonly IAvatarService _avatars;

    public UsersController(IUserService users, IPreferenceService preferences, IAvatarService avatars)
    {
        _users = users;
        _preferences = preferences;
        _avatars = avatars;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<UserResponse>>> GetUsers([FromQuery] UserQuery query, CancellationToken cancellationToken) =>
        FromResult(await _users.GetUsersAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserDetailResponse>> GetUser(Guid id, CancellationToken cancellationToken) =>
        FromResult(await _users.GetUserAsync(id, cancellationToken));

    [HttpGet("me/profile")]
    public async Task<ActionResult<OwnProfileResponse>> GetOwnProfile(CancellationToken cancellationToken) =>
        FromResult(await _users.GetOwnProfileAsync(cancellationToken));

    [HttpPut("me/profile")]
    public async Task<ActionResult<OwnProfileResponse>> UpdateOwnProfile(
        UpdateOwnProfileRequest request,
        CancellationToken cancellationToken) =>
        FromResult(await _users.UpdateOwnProfileAsync(request, cancellationToken));

    /// <summary>上傳本人已剪裁為 1080 × 1080 的大頭貼。</summary>
    [HttpPut("me/avatar")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 10 * 1024 * 1024)]
    public async Task<IActionResult> UploadOwnAvatar(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return FromError(new ServiceError("avatar_required", "請選擇圖片。", 400));
        }
        if (file.Length == 0 || file.Length > 10 * 1024 * 1024)
        {
            return FromError(new ServiceError("avatar_size_invalid", "圖片檔案大小不符合限制。", 413));
        }

        await using var stream = new MemoryStream((int)file.Length);
        await file.CopyToAsync(stream, cancellationToken);
        ServiceResult<AvatarImageResponse> result = await _avatars.UploadOwnAsync(
            stream.ToArray(), file.FileName, file.ContentType, cancellationToken);
        return result.IsSuccess ? NoContent() : FromError(result.Error!);
    }

    /// <summary>讀取本人或有權查看的使用者大頭貼。</summary>
    [HttpGet("{id:guid}/avatar")]
    public async Task<IActionResult> GetAvatar(Guid id, CancellationToken cancellationToken)
    {
        ServiceResult<AvatarImageResponse> result = await _avatars.GetAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return FromError(result.Error!);
        }
        if (result.Value is not AvatarImageResponse image)
        {
            return StatusCode(500);
        }
        Response.Headers.CacheControl = "no-store";
        return File(image.Content, image.ContentType);
    }

    [HttpPut("{id:guid}/role")]
    public async Task<ActionResult<UserResponse>> ReplaceRole(Guid id, UpdateRoleRequest request, CancellationToken cancellationToken) =>
        FromResult(await _users.ReplaceRoleAsync(id, request, cancellationToken));

    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<UserResponse>> UpdateStatus(Guid id, UpdateUserStatusRequest request, CancellationToken cancellationToken) =>
        FromResult(await _users.UpdateStatusAsync(id, request, cancellationToken));

    [HttpPut("{id:guid}/administration")]
    public async Task<ActionResult<UserResponse>> UpdateAdministration(Guid id, UpdateAdministrationRequest request,
        CancellationToken cancellationToken) =>
        FromResult(await _users.UpdateAdministrationAsync(id, request, cancellationToken));

    [HttpGet("me/preferences")]
    public async Task<ActionResult<PreferenceResponse>> GetPreferences(CancellationToken cancellationToken) =>
        FromResult(await _preferences.GetAsync(cancellationToken));

    [HttpPut("me/preferences")]
    public async Task<ActionResult<PreferenceResponse>> UpdatePreferences(UpdatePreferenceRequest request, CancellationToken cancellationToken) =>
        FromResult(await _preferences.UpdateAsync(request, cancellationToken));
}
