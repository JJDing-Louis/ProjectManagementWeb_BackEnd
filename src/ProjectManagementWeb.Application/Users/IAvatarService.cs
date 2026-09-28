using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.Application.Users;

public interface IAvatarService
{
    Task<ServiceResult<AvatarImageResponse>> UploadOwnAsync(
        byte[] content, string fileName, string contentType, CancellationToken cancellationToken);
    Task<ServiceResult<AvatarImageResponse>> GetAsync(Guid accountId, CancellationToken cancellationToken);
}
