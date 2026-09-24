using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectManagementWeb.Application.Comments;

namespace ProjectManagementWeb.Api.Controllers;

[Authorize]
[Route("api/v1/projects/{projectId:guid}/task-items/{taskId:guid}/comments")]
public sealed class CommentsController : ApiControllerBase
{
    private readonly ICommentService _comments;

    public CommentsController(ICommentService comments) => _comments = comments;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<CommentResponse>>> GetComments(Guid projectId, Guid taskId,
        CancellationToken cancellationToken) => FromResult(await _comments.GetCommentsAsync(projectId, taskId, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<CommentResponse>> CreateComment(Guid projectId, Guid taskId, CreateCommentRequest request,
        CancellationToken cancellationToken) => FromResult(await _comments.CreateCommentAsync(projectId, taskId, request, cancellationToken));

    [HttpPut("{commentId:guid}")]
    public async Task<ActionResult<CommentResponse>> UpdateComment(Guid projectId, Guid taskId, Guid commentId,
        UpdateCommentRequest request, CancellationToken cancellationToken) =>
        FromResult(await _comments.UpdateCommentAsync(projectId, taskId, commentId, request, cancellationToken));

    [HttpDelete("{commentId:guid}")]
    public async Task<IActionResult> DeleteComment(Guid projectId, Guid taskId, Guid commentId, [FromQuery] string rowVersion,
        CancellationToken cancellationToken)
    {
        var result = await _comments.DeleteCommentAsync(projectId, taskId, commentId, rowVersion, cancellationToken);
        return result.IsSuccess ? NoContent() : FromResult(result).Result!;
    }
}
