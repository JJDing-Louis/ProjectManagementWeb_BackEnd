namespace ProjectManagementWeb.Domain.Entities;

public sealed class ProjectMemberRole
{
    private ProjectMemberRole() { }

    public ProjectMemberRole(Guid projectId, Guid accountId, Guid projectRoleId)
    {
        ProjectId = projectId;
        AccountId = accountId;
        ProjectRoleId = projectRoleId;
    }

    public Guid ProjectId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid ProjectRoleId { get; private set; }
}
