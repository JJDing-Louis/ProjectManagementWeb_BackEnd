using Microsoft.AspNetCore.Identity;

namespace ProjectManagementWeb.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string? Name { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int TokenVersion { get; set; }
    public string? Remark { get; set; }
}
