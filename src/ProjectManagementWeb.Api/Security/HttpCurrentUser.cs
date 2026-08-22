using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.Api.Security;

internal sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public HttpCurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;
    public Guid? AccountId => Guid.TryParse(Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub), out Guid id) ? id : null;
    public string? Role => Principal?.FindFirstValue("role");
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public bool HasFunction(string functionCode) => Principal?.FindAll("function").Any(x => x.Value == functionCode) == true;
}
