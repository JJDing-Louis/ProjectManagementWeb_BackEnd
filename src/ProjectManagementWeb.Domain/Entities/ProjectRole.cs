namespace ProjectManagementWeb.Domain.Entities;

public sealed class ProjectRole
{
    private ProjectRole() { }

    public ProjectRole(Guid id, string code, string name)
    {
        Id = id;
        Code = code;
        Name = name;
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
}
