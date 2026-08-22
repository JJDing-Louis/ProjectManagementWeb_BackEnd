using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.Application.Tasks;

public interface ITaskService
{
    Task<ServiceResult<PagedResult<TaskResponse>>> GetTasksAsync(Guid projectId, TaskQuery query, CancellationToken cancellationToken);
    Task<ServiceResult<TaskResponse>> GetTaskAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken);
    Task<ServiceResult<TaskResponse>> CreateTaskAsync(Guid projectId, CreateTaskRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<TaskResponse>> UpdateTaskAsync(Guid projectId, Guid taskId, UpdateTaskRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<TaskResponse>> UpdateAssignedTaskAsync(Guid projectId, Guid taskId, UpdateAssignedTaskRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<BatchUpdateResponse>> BatchUpdateStatusAsync(Guid projectId, BatchUpdateTaskStatusRequest request, CancellationToken cancellationToken);
    Task<ServiceResult<bool>> DeleteTaskAsync(Guid projectId, Guid taskId, string rowVersion, CancellationToken cancellationToken);
}
