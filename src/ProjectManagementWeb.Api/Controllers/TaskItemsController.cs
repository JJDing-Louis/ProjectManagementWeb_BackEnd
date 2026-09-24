using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Application.Tasks;

namespace ProjectManagementWeb.Api.Controllers;

[Authorize]
[Route("api/v1/projects/{projectId:guid}/task-items")]
public sealed class TaskItemsController : ApiControllerBase
{
    private readonly ITaskService _tasks;

    public TaskItemsController(ITaskService tasks) => _tasks = tasks;

    [HttpGet]
    public async Task<ActionResult<PagedResult<TaskResponse>>> GetTasks(Guid projectId, [FromQuery] TaskQuery query,
        CancellationToken cancellationToken) => FromResult(await _tasks.GetTasksAsync(projectId, query, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<TaskResponse>> CreateTask(Guid projectId, CreateTaskRequest request, CancellationToken cancellationToken)
    {
        var result = await _tasks.CreateTaskAsync(projectId, request, cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetTask), new { projectId, taskId = result.Value!.Id }, result.Value)
            : FromResult(result);
    }

    [HttpGet("{taskId:guid}")]
    public async Task<ActionResult<TaskResponse>> GetTask(Guid projectId, Guid taskId, CancellationToken cancellationToken) =>
        FromResult(await _tasks.GetTaskAsync(projectId, taskId, cancellationToken));

    [HttpPut("{taskId:guid}")]
    public async Task<ActionResult<TaskResponse>> UpdateTask(Guid projectId, Guid taskId, UpdateTaskRequest request,
        CancellationToken cancellationToken) => FromResult(await _tasks.UpdateTaskAsync(projectId, taskId, request, cancellationToken));

    [HttpPatch("{taskId:guid}/status-and-deadline")]
    public async Task<ActionResult<TaskResponse>> UpdateAssignedTask(Guid projectId, Guid taskId,
        UpdateAssignedTaskRequest request, CancellationToken cancellationToken) =>
        FromResult(await _tasks.UpdateAssignedTaskAsync(projectId, taskId, request, cancellationToken));

    [HttpPatch("batch-status")]
    public async Task<ActionResult<BatchUpdateResponse>> BatchUpdateStatus(Guid projectId,
        BatchUpdateTaskStatusRequest request, CancellationToken cancellationToken) =>
        FromResult(await _tasks.BatchUpdateStatusAsync(projectId, request, cancellationToken));

    [HttpDelete("{taskId:guid}")]
    public async Task<IActionResult> DeleteTask(Guid projectId, Guid taskId, [FromQuery] string rowVersion,
        CancellationToken cancellationToken)
    {
        var result = await _tasks.DeleteTaskAsync(projectId, taskId, rowVersion, cancellationToken);
        return result.IsSuccess ? NoContent() : FromResult(result).Result!;
    }
}
