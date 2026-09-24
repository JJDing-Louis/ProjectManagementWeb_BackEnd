using Microsoft.AspNetCore.Identity;

namespace ProjectManagementWeb.Infrastructure.Identity;

public sealed class ApplicationRole : IdentityRole<Guid>
{
    public string? Description { get; set; }
}
