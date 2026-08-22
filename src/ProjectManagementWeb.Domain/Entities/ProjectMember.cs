namespace ProjectManagementWeb.Domain.Entities;

public sealed class ProjectMember
{
    private ProjectMember() { }

    public ProjectMember(Guid projectId, Guid accountId, DateTimeOffset joinedAt)
    {
        ProjectId = projectId;
        AccountId = accountId;
        JoinedAt = joinedAt;
    }

    public Guid ProjectId { get; private set; }
    public Guid AccountId { get; private set; }
    public DateTimeOffset JoinedAt { get; private set; }
}
