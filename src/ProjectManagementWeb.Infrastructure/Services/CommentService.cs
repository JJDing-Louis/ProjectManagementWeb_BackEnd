using Microsoft.EntityFrameworkCore;
using ProjectManagementWeb.Application.Comments;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.Infrastructure.Services;

internal sealed class CommentService : ICommentService
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ServiceSupport _support;
    private readonly TimeProvider _timeProvider;

    public CommentService(ApplicationDbContext db, ICurrentUser currentUser, ServiceSupport support, TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _support = support;
        _timeProvider = timeProvider;
    }

    public async Task<ServiceResult<IReadOnlyCollection<CommentResponse>>> GetCommentsAsync(
        Guid projectId, Guid taskId, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.CommentsRead) || !await TaskAccessibleAsync(projectId, taskId, cancellationToken))
        {
            return ServiceResult<IReadOnlyCollection<CommentResponse>>.Failure("forbidden", "沒有讀取留言的權限。", 403);
        }
        TaskItemComment[] rows = await _db.TaskItemComments.AsNoTracking()
            .Where(x => x.TaskItemId == taskId).OrderBy(x => x.CreatedAt).ToArrayAsync(cancellationToken);
        CommentResponse[] comments = rows.Select(Map).ToArray();
        return ServiceResult<IReadOnlyCollection<CommentResponse>>.Success(comments);
    }

    public async Task<ServiceResult<CommentResponse>> CreateCommentAsync(
        Guid projectId, Guid taskId, CreateCommentRequest request, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.CommentsCreate) || _currentUser.AccountId is not Guid authorId ||
            !await TaskAccessibleAsync(projectId, taskId, cancellationToken))
        {
            return ServiceResult<CommentResponse>.Failure("forbidden", "沒有新增留言的權限。", 403);
        }
        ServiceError? validation = ValidateContent(request.Content);
        if (validation is not null)
        {
            return ServiceResult<CommentResponse>.Failure(validation.Code, validation.Message, validation.StatusCode);
        }
        var comment = new TaskItemComment(Guid.NewGuid(), taskId, authorId, request.Content.Trim(), _timeProvider.GetUtcNow());
        _db.TaskItemComments.Add(comment);
        _support.AddAudit("Create", "TaskItemComment", comment.Id.ToString(), null, new { comment.TaskItemId, comment.Content }, _timeProvider.GetUtcNow());
        await _db.SaveChangesAsync(cancellationToken);
        return ServiceResult<CommentResponse>.Success(Map(comment));
    }

    public async Task<ServiceResult<CommentResponse>> UpdateCommentAsync(
        Guid projectId, Guid taskId, Guid commentId, UpdateCommentRequest request, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.CommentsUpdateOwn) || _currentUser.AccountId is not Guid authorId ||
            !await TaskAccessibleAsync(projectId, taskId, cancellationToken))
        {
            return ServiceResult<CommentResponse>.Failure("forbidden", "沒有修改留言的權限。", 403);
        }
        TaskItemComment? comment = await _db.TaskItemComments.SingleOrDefaultAsync(x => x.Id == commentId && x.TaskItemId == taskId, cancellationToken);
        if (comment is null)
        {
            return ServiceResult<CommentResponse>.Failure("not_found", "找不到留言。", 404);
        }
        if (comment.AuthorAccountId != authorId)
        {
            return ServiceResult<CommentResponse>.Failure("forbidden", "只能修改自己的留言。", 403);
        }
        ServiceError? validation = ValidateContent(request.Content);
        if (validation is not null)
        {
            return ServiceResult<CommentResponse>.Failure(validation.Code, validation.Message, validation.StatusCode);
        }
        if (!VersionMatches(comment.RowVersion, request.RowVersion))
        {
            return ServiceResult<CommentResponse>.Failure("concurrency_conflict", "留言已被更新，請重新載入。", 409);
        }
        string before = comment.Content;
        comment.Update(request.Content.Trim(), _timeProvider.GetUtcNow());
        _support.AddAudit("Update", "TaskItemComment", comment.Id.ToString(), new { Content = before }, new { comment.Content }, _timeProvider.GetUtcNow());
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult<CommentResponse>.Failure("concurrency_conflict", "留言已被更新，請重新載入。", 409);
        }
        return ServiceResult<CommentResponse>.Success(Map(comment));
    }

    public async Task<ServiceResult<bool>> DeleteCommentAsync(
        Guid projectId, Guid taskId, Guid commentId, string rowVersion, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.CommentsDeleteOwn) || _currentUser.AccountId is not Guid authorId ||
            !await TaskAccessibleAsync(projectId, taskId, cancellationToken))
        {
            return ServiceResult<bool>.Failure("forbidden", "沒有刪除留言的權限。", 403);
        }
        TaskItemComment? comment = await _db.TaskItemComments.SingleOrDefaultAsync(x => x.Id == commentId && x.TaskItemId == taskId, cancellationToken);
        if (comment is null)
        {
            return ServiceResult<bool>.Failure("not_found", "找不到留言。", 404);
        }
        if (comment.AuthorAccountId != authorId)
        {
            return ServiceResult<bool>.Failure("forbidden", "只能刪除自己的留言。", 403);
        }
        if (!VersionMatches(comment.RowVersion, rowVersion))
        {
            return ServiceResult<bool>.Failure("concurrency_conflict", "留言已被更新，請重新載入。", 409);
        }
        comment.SoftDelete(_timeProvider.GetUtcNow());
        _support.AddAudit("Delete", "TaskItemComment", comment.Id.ToString(), new { comment.Content }, null, _timeProvider.GetUtcNow());
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult<bool>.Failure("concurrency_conflict", "留言已被更新，請重新載入。", 409);
        }
        return ServiceResult<bool>.Success(true);
    }

    private async Task<bool> TaskAccessibleAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken) =>
        await _support.CanReadProjectAsync(projectId, cancellationToken) &&
        await _db.TaskItems.AnyAsync(x => x.ProjectId == projectId && x.Id == taskId, cancellationToken);

    private static ServiceError? ValidateContent(string content) =>
        string.IsNullOrWhiteSpace(content) || content.Trim().Length > 2000
            ? new ServiceError("validation_error", "留言內容必須為 1 至 2000 字。", 400)
            : null;

    private static bool VersionMatches(byte[] current, string supplied) =>
        ServiceSupport.TryDecodeRowVersion(supplied, out byte[] version) && current.SequenceEqual(version);

    private static CommentResponse Map(TaskItemComment comment) => new(comment.Id, comment.TaskItemId,
        comment.AuthorAccountId, comment.Content, comment.CreatedAt, comment.UpdatedAt, Convert.ToBase64String(comment.RowVersion));
}
