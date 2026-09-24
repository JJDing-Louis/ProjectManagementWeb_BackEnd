namespace ProjectManagementWeb.Domain.Entities;

public sealed class RoleFunction
{
    private RoleFunction() { }

    public RoleFunction(Guid roleId, Guid functionId)
    {
        RoleId = roleId;
        FunctionId = functionId;
    }

    public Guid RoleId { get; private set; }
    public Guid FunctionId { get; private set; }
}
