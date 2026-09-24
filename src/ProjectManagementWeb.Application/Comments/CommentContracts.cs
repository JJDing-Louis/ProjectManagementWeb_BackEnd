namespace ProjectManagementWeb.Application.Comments;

public sealed record CreateCommentRequest(string Content);
public sealed record UpdateCommentRequest(string Content, string RowVersion);
public sealed record CommentResponse(Guid Id, Guid TaskItemId, Guid AuthorAccountId, string Content,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string RowVersion);
