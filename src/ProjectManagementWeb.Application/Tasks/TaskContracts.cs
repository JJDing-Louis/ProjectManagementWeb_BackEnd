using ProjectManagementWeb.Application.Common;
using TaskStatus = ProjectManagementWeb.Domain.Enums.TaskStatus;

namespace ProjectManagementWeb.Application.Tasks;

public sealed record TaskQuery(string? Search, TaskStatus? Status, Guid? AssignedAccountId, bool OnlyMine,
    string SortBy = "createdAt", string SortDirection = "desc", int Page = 1, int PageSize = 20);
public sealed record CreateTaskRequest(string Title, string? Description, Guid AssignedAccountId,
    DateTimeOffset StartAt, DateTimeOffset Deadline);
public sealed record UpdateTaskRequest(string Title, string? Description, Guid AssignedAccountId, DateTimeOffset StartAt,
    DateTimeOffset Deadline, TaskStatus Status, string RowVersion);
public sealed record UpdateAssignedTaskRequest(TaskStatus Status, DateTimeOffset Deadline, string RowVersion);
public sealed record BatchUpdateTaskStatusRequest(IReadOnlyCollection<BatchTaskVersion> Tasks, TaskStatus TargetStatus);
public sealed record BatchTaskVersion(Guid TaskId, string RowVersion);
public sealed record TaskResponse(Guid Id, string Code, Guid ProjectId, string Title, string? Description,
    Guid CreatedByAccountId, Guid AssignedAccountId, DateTimeOffset StartAt, DateTimeOffset Deadline, TaskStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string RowVersion);
public sealed record TaskListResponse(PagedResult<TaskResponse> Result);
public sealed record BatchUpdateResponse(int UpdatedCount);
