using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.Application.Comments;

public interface ICommentService
{
    Task<ServiceResult<IReadOnlyCollection<CommentResponse>>> GetCommentsAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken);
    Task<ServiceResult<CommentResponse>> CreateCommentAsync(Guid projectId, Guid taskId, CreateCommentRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<CommentResponse>> UpdateCommentAsync(Guid projectId, Guid taskId, Guid commentId, UpdateCommentRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> DeleteCommentAsync(Guid projectId, Guid taskId, Guid commentId, string rowVersion, CancellationToken cancellationToken);
}
