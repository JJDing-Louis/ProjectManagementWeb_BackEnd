using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.Infrastructure.Services;

internal sealed class ServiceSupport
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;

    public ServiceSupport(ApplicationDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public Guid CurrentAccountId => _currentUser.AccountId ?? throw new InvalidOperationException("目前請求未登入。");
    public bool HasFunction(string code) => _currentUser.HasFunction(code);
    public bool IsViewer => string.Equals(_currentUser.Role, SystemRoles.Viewer, StringComparison.Ordinal);

    public Task<bool> CanReadProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        HasFunction(SystemFunctions.ProjectsManageAll)
            ? Task.FromResult(true)
            : _db.ProjectMembers.AnyAsync(x => x.ProjectId == projectId && x.AccountId == CurrentAccountId, cancellationToken);

    public async Task<bool> CanManageProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (IsViewer)
        {
            return false;
        }
        if (HasFunction(SystemFunctions.ProjectsManageAll))
        {
            return true;
        }
        Guid managerRoleId = SeedIds.Create($"project-role:{ProjectRoleCodes.ProjectManager}");
        return await _db.ProjectMemberRoles.AnyAsync(x => x.ProjectId == projectId &&
            x.AccountId == CurrentAccountId && x.ProjectRoleId == managerRoleId, cancellationToken);
    }

    public void AddAudit(string action, string entityType, string entityId, object? before, object? after, DateTimeOffset now) =>
        _db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), _currentUser.AccountId, action, entityType, entityId,
            before is null ? null : JsonSerializer.Serialize(before),
            after is null ? null : JsonSerializer.Serialize(after), now));

    public static bool TryDecodeRowVersion(string value, out byte[] rowVersion)
    {
        try
        {
            rowVersion = Convert.FromBase64String(value);
            return rowVersion.Length > 0;
        }
        catch (FormatException)
        {
            rowVersion = [];
            return false;
        }
    }
}
