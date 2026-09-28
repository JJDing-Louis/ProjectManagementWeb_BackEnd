using Microsoft.EntityFrameworkCore;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Application.Users;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.Infrastructure.Services;

internal sealed class AvatarService : IAvatarService
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly AvatarImageProcessor _processor;
    private readonly ServiceSupport _support;
    private readonly TimeProvider _timeProvider;

    public AvatarService(ApplicationDbContext db, ICurrentUser currentUser, AvatarImageProcessor processor,
        ServiceSupport support, TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _processor = processor;
        _support = support;
        _timeProvider = timeProvider;
    }

    public async Task<ServiceResult<AvatarImageResponse>> UploadOwnAsync(
        byte[] content, string fileName, string contentType, CancellationToken cancellationToken)
    {
        if (_currentUser.AccountId is not Guid accountId)
        {
            return ServiceResult<AvatarImageResponse>.Failure("unauthorized", "尚未登入。", 401);
        }

        bool? emailConfirmed = await _db.Users.AsNoTracking().Where(user => user.Id == accountId)
            .Select(user => (bool?)user.EmailConfirmed).SingleOrDefaultAsync(cancellationToken);
        if (emailConfirmed is null)
        {
            return ServiceResult<AvatarImageResponse>.Failure("not_found", "找不到帳號。", 404);
        }
        if (emailConfirmed is false)
        {
            return ServiceResult<AvatarImageResponse>.Failure("email_not_verified", "請先完成信箱驗證。", 403);
        }

        ServiceResult<AvatarImageResponse> processed = _processor.Process(content, fileName, contentType);
        if (!processed.IsSuccess)
        {
            return processed;
        }

        AvatarImageResponse? image = processed.Value;
        if (image is null)
        {
            return ServiceResult<AvatarImageResponse>.Failure("avatar_processing_failed", "圖片處理失敗。", 500);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        int updated = await _db.Users.Where(user => user.Id == accountId && user.EmailConfirmed)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.AvatarImage, image.Content), cancellationToken);
        if (updated != 1)
        {
            return ServiceResult<AvatarImageResponse>.Failure("email_not_verified", "請先完成信箱驗證。", 403);
        }
        _support.AddAudit("UpdateOwnAvatar", "Account", accountId.ToString(), null,
            new { ImageUpdated = true }, _timeProvider.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<AvatarImageResponse>.Success(image);
    }

    public async Task<ServiceResult<AvatarImageResponse>> GetAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (_currentUser.AccountId != accountId && !_currentUser.HasFunction(SystemFunctions.AccountsRead))
        {
            return ServiceResult<AvatarImageResponse>.Failure("forbidden", "沒有查詢帳號的權限。", 403);
        }

        byte[]? image = await _db.Users.AsNoTracking().Where(user => user.Id == accountId)
            .Select(user => user.AvatarImage).SingleOrDefaultAsync(cancellationToken);
        if (image is null)
        {
            return ServiceResult<AvatarImageResponse>.Failure("not_found", "找不到大頭貼。", 404);
        }

        string contentType = AvatarImageProcessor.IsJpeg(image) ? "image/jpeg" : "image/png";
        return ServiceResult<AvatarImageResponse>.Success(new AvatarImageResponse(image, contentType));
    }
}
