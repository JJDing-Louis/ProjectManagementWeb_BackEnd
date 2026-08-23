using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Application.Tasks;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Domain.Enums;
using ProjectManagementWeb.Infrastructure.Persistence;
using TaskStatus = ProjectManagementWeb.Domain.Enums.TaskStatus;

namespace ProjectManagementWeb.Infrastructure.Services;

internal sealed class TaskService : ITaskService
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ServiceSupport _support;
    private readonly TimeProvider _timeProvider;
    private readonly IBusinessCodeGenerator _codeGenerator;

    public TaskService(ApplicationDbContext db, ICurrentUser currentUser, ServiceSupport support,
        TimeProvider timeProvider, IBusinessCodeGenerator codeGenerator)
    {
        _db = db;
        _currentUser = currentUser;
        _support = support;
        _timeProvider = timeProvider;
        _codeGenerator = codeGenerator;
    }

    public async Task<ServiceResult<PagedResult<TaskResponse>>> GetTasksAsync(Guid projectId, TaskQuery query, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.TasksRead) || !await _support.CanReadProjectAsync(projectId, cancellationToken))
        {
            return ServiceResult<PagedResult<TaskResponse>>.Failure("forbidden", "沒有讀取 Task 的權限。", 403);
        }
        int page = Math.Max(query.Page, 1);
        int pageSize = Math.Clamp(query.PageSize, 1, 100);
        IQueryable<TaskItem> tasks = _db.TaskItems.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string search = query.Search.Trim();
            tasks = tasks.Where(x => x.Code.Contains(search) || x.Title.Contains(search) ||
                                     (x.Description != null && x.Description.Contains(search)));
        }
        if (query.Status is not null)
        {
            tasks = tasks.Where(x => x.Status == query.Status.Value);
        }
        if (query.AssignedAccountId is not null)
        {
            tasks = tasks.Where(x => x.AssignedAccountId == query.AssignedAccountId);
        }
        if (query.OnlyMine && _currentUser.AccountId is Guid accountId)
        {
            tasks = tasks.Where(x => x.AssignedAccountId == accountId);
        }
        tasks = ApplySort(tasks, query.SortBy, query.SortDirection);
        int total = await tasks.CountAsync(cancellationToken);
        TaskItem[] rows = await tasks.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);
        TaskResponse[] items = rows.Select(Map).ToArray();
        return ServiceResult<PagedResult<TaskResponse>>.Success(new PagedResult<TaskResponse>(items, page, pageSize, total));
    }

    public async Task<ServiceResult<TaskResponse>> GetTaskAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.TasksRead) || !await _support.CanReadProjectAsync(projectId, cancellationToken))
        {
            return ServiceResult<TaskResponse>.Failure("forbidden", "沒有讀取 Task 的權限。", 403);
        }
        TaskItem? task = await _db.TaskItems.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == taskId, cancellationToken);
        return task is null
            ? ServiceResult<TaskResponse>.Failure("not_found", "找不到 Task。", 404)
            : ServiceResult<TaskResponse>.Success(Map(task));
    }

    public async Task<ServiceResult<TaskResponse>> CreateTaskAsync(Guid projectId, CreateTaskRequest request, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.TasksCreate) || _currentUser.AccountId is not Guid actorId ||
            !await _support.CanReadProjectAsync(projectId, cancellationToken))
        {
            return ServiceResult<TaskResponse>.Failure("forbidden", "沒有建立 Task 的權限。", 403);
        }
        ServiceError? validation = await ValidateTaskAsync(projectId, request.Title, request.AssignedAccountId, request.StartAt, request.Deadline, cancellationToken);
        if (validation is not null)
        {
            return ServiceResult<TaskResponse>.Failure(validation.Code, validation.Message, validation.StatusCode);
        }
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        string? code = await _codeGenerator.GenerateAsync(BusinessCodeType.Task, now, cancellationToken);
        if (code is null)
        {
            return ServiceResult<TaskResponse>.Failure("daily_code_limit_exceeded", "今日 Task 編號已達上限。", 409);
        }
        var task = new TaskItem(Guid.NewGuid(), code, projectId, actorId, request.AssignedAccountId,
            request.Title.Trim(), request.Description?.Trim(), request.StartAt, request.Deadline, now);
        _db.TaskItems.Add(task);
        AddHistory(task, actorId, "Create", now);
        _support.AddAudit("Create", "TaskItem", task.Id.ToString(), null, Snapshot(task), now);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ServiceResult<TaskResponse>.Failure("duplicate_task_code", "Task 編號已存在。", 409);
        }
        return ServiceResult<TaskResponse>.Success(Map(task));
    }

    public async Task<ServiceResult<TaskResponse>> UpdateTaskAsync(Guid projectId, Guid taskId, UpdateTaskRequest request, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.TasksUpdateAny) || _currentUser.AccountId is not Guid actorId ||
            !await _support.CanReadProjectAsync(projectId, cancellationToken))
        {
            return ServiceResult<TaskResponse>.Failure("forbidden", "沒有修改完整 Task 的權限。", 403);
        }
        TaskItem? task = await _db.TaskItems.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == taskId, cancellationToken);
        if (task is null)
        {
            return ServiceResult<TaskResponse>.Failure("not_found", "找不到 Task。", 404);
        }
        ServiceError? versionError = ValidateVersion(task.RowVersion, request.RowVersion);
        if (versionError is not null)
        {
            return ServiceResult<TaskResponse>.Failure(versionError.Code, versionError.Message, versionError.StatusCode);
        }
        ServiceError? validation = await ValidateTaskAsync(projectId, request.Title, request.AssignedAccountId, request.StartAt, request.Deadline, cancellationToken);
        if (validation is not null)
        {
            return ServiceResult<TaskResponse>.Failure(validation.Code, validation.Message, validation.StatusCode);
        }
        object before = Snapshot(task);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        task.Update(request.Title.Trim(), request.Description?.Trim(), request.AssignedAccountId,
            request.StartAt, request.Deadline, request.Status, now);
        AddHistory(task, actorId, "Update", now);
        _support.AddAudit("Update", "TaskItem", task.Id.ToString(), before, Snapshot(task), now);
        return await SaveTaskAsync(task, cancellationToken);
    }

    public async Task<ServiceResult<TaskResponse>> UpdateAssignedTaskAsync(Guid projectId, Guid taskId,
        UpdateAssignedTaskRequest request, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.TasksUpdateAssigned) || _currentUser.AccountId is not Guid actorId ||
            !await _support.CanReadProjectAsync(projectId, cancellationToken))
        {
            return ServiceResult<TaskResponse>.Failure("forbidden", "沒有修改被指派 Task 的權限。", 403);
        }
        TaskItem? task = await _db.TaskItems.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == taskId, cancellationToken);
        if (task is null)
        {
            return ServiceResult<TaskResponse>.Failure("not_found", "找不到 Task。", 404);
        }
        if (task.AssignedAccountId != actorId)
        {
            return ServiceResult<TaskResponse>.Failure("forbidden", "只能修改指派給自己的 Task。", 403);
        }
        ServiceError? versionError = ValidateVersion(task.RowVersion, request.RowVersion);
        if (versionError is not null)
        {
            return ServiceResult<TaskResponse>.Failure(versionError.Code, versionError.Message, versionError.StatusCode);
        }
        if (request.Deadline < task.StartAt)
        {
            return ServiceResult<TaskResponse>.Failure("invalid_deadline", "交付期限不得早於開始時間。", 422);
        }
        object before = Snapshot(task);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        task.UpdateStatusAndDeadline(request.Status, request.Deadline, now);
        AddHistory(task, actorId, "UpdateAssigned", now);
        _support.AddAudit("UpdateAssigned", "TaskItem", task.Id.ToString(), before, Snapshot(task), now);
        return await SaveTaskAsync(task, cancellationToken);
    }

    public async Task<ServiceResult<BatchUpdateResponse>> BatchUpdateStatusAsync(Guid projectId,
        BatchUpdateTaskStatusRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.AccountId is not Guid actorId || request.Tasks.Count == 0 ||
            !await _support.CanReadProjectAsync(projectId, cancellationToken))
        {
            return ServiceResult<BatchUpdateResponse>.Failure("validation_error", "請至少選擇一筆可修改的 Task。", 400);
        }
        Guid[] ids = request.Tasks.Select(x => x.TaskId).Distinct().ToArray();
        if (ids.Length != request.Tasks.Count)
        {
            return ServiceResult<BatchUpdateResponse>.Failure("duplicate_task", "批次資料包含重複 Task。", 400);
        }
        List<TaskItem> tasks = await _db.TaskItems.Where(x => x.ProjectId == projectId && ids.Contains(x.Id)).ToListAsync(cancellationToken);
        if (tasks.Count != ids.Length)
        {
            return ServiceResult<BatchUpdateResponse>.Failure("not_found", "批次資料包含不存在的 Task。", 404);
        }
        bool canUpdateAny = _currentUser.HasFunction(SystemFunctions.TasksUpdateAny);
        bool canUpdateAssigned = _currentUser.HasFunction(SystemFunctions.TasksUpdateAssigned);
        foreach (TaskItem task in tasks)
        {
            if (!canUpdateAny && (!canUpdateAssigned || task.AssignedAccountId != actorId))
            {
                return ServiceResult<BatchUpdateResponse>.Failure("forbidden", "批次資料包含無權修改的 Task。", 403);
            }
            string version = request.Tasks.Single(x => x.TaskId == task.Id).RowVersion;
            ServiceError? error = ValidateVersion(task.RowVersion, version);
            if (error is not null)
            {
                return ServiceResult<BatchUpdateResponse>.Failure(error.Code, error.Message, error.StatusCode);
            }
        }
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (TaskItem task in tasks)
        {
            object before = Snapshot(task);
            task.UpdateStatus(request.TargetStatus, now);
            AddHistory(task, actorId, "BatchUpdateStatus", now);
            _support.AddAudit("BatchUpdateStatus", "TaskItem", task.Id.ToString(), before, Snapshot(task), now);
        }
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ServiceResult<BatchUpdateResponse>.Failure("concurrency_conflict", "批次資料已變更，全部資料均未更新。", 409);
        }
        return ServiceResult<BatchUpdateResponse>.Success(new BatchUpdateResponse(tasks.Count));
    }

    public async Task<ServiceResult<bool>> DeleteTaskAsync(Guid projectId, Guid taskId, string rowVersion, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.TasksDelete) || _currentUser.AccountId is not Guid actorId ||
            !await _support.CanReadProjectAsync(projectId, cancellationToken))
        {
            return ServiceResult<bool>.Failure("forbidden", "沒有刪除 Task 的權限。", 403);
        }
        TaskItem? task = await _db.TaskItems.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.Id == taskId, cancellationToken);
        if (task is null)
        {
            return ServiceResult<bool>.Failure("not_found", "找不到 Task。", 404);
        }
        ServiceError? error = ValidateVersion(task.RowVersion, rowVersion);
        if (error is not null)
        {
            return ServiceResult<bool>.Failure(error.Code, error.Message, error.StatusCode);
        }
        object before = Snapshot(task);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        task.SoftDelete(now);
        AddHistory(task, actorId, "Delete", now);
        _support.AddAudit("Delete", "TaskItem", task.Id.ToString(), before, Snapshot(task), now);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult<bool>.Failure("concurrency_conflict", "Task 已被其他使用者更新。", 409);
        }
        return ServiceResult<bool>.Success(true);
    }

    private async Task<ServiceResult<TaskResponse>> SaveTaskAsync(TaskItem task, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return ServiceResult<TaskResponse>.Success(Map(task));
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult<TaskResponse>.Failure("concurrency_conflict", "Task 已被其他使用者更新，請重新載入。", 409);
        }
    }

    private async Task<ServiceError?> ValidateTaskAsync(Guid projectId, string title, Guid assignedAccountId,
        DateTimeOffset startAt, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 300)
        {
            return new ServiceError("validation_error", "標題為必填且不得超過 300 字。", 400);
        }
        if (startAt > deadline)
        {
            return new ServiceError("invalid_deadline", "開始時間不得晚於交付期限。", 422);
        }
        bool validMember = await _db.ProjectMembers.AnyAsync(x => x.ProjectId == projectId && x.AccountId == assignedAccountId, cancellationToken);
        return validMember ? null : new ServiceError("invalid_assignee", "指派對象必須是有效的專案成員。", 422);
    }

    private void AddHistory(TaskItem task, Guid actorId, string action, DateTimeOffset now) =>
        _db.TaskItemHistories.Add(new TaskItemHistory(Guid.NewGuid(), task.Id, actorId, action,
            JsonSerializer.Serialize(Snapshot(task)), now));

    private static ServiceError? ValidateVersion(byte[] current, string supplied) =>
        ServiceSupport.TryDecodeRowVersion(supplied, out byte[] version) && current.SequenceEqual(version)
            ? null
            : new ServiceError("concurrency_conflict", "Task 已被其他使用者更新，請重新載入。", 409);

    private static object Snapshot(TaskItem task) => new
    {
        task.Id,
        task.Code,
        task.ProjectId,
        task.Title,
        task.Description,
        task.AssignedAccountId,
        task.StartAt,
        task.Deadline,
        task.Status,
        task.DeletedAt
    };

    private static IQueryable<TaskItem> ApplySort(IQueryable<TaskItem> query, string sortBy, string direction)
    {
        bool ascending = string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase);
        return sortBy.ToLowerInvariant() switch
        {
            "deadline" => ascending ? query.OrderBy(x => x.Deadline) : query.OrderByDescending(x => x.Deadline),
            "status" => ascending ? query.OrderBy(x => x.Status) : query.OrderByDescending(x => x.Status),
            "code" => ascending ? query.OrderBy(x => x.Code) : query.OrderByDescending(x => x.Code),
            _ => ascending ? query.OrderBy(x => x.CreatedAt) : query.OrderByDescending(x => x.CreatedAt)
        };
    }

    private static TaskResponse Map(TaskItem task) => new(task.Id, task.Code, task.ProjectId, task.Title, task.Description,
        task.CreatedByAccountId, task.AssignedAccountId, task.StartAt, task.Deadline, task.Status,
        task.CreatedAt, task.UpdatedAt, Convert.ToBase64String(task.RowVersion));
}
