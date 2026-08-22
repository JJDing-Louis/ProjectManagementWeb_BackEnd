using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Infrastructure.Identity;

namespace ProjectManagementWeb.Infrastructure.Persistence;

public static class DatabaseBootstrapper
{
    public static async Task EnsureBootstrapAdminAsync(
        this IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        string? account = configuration["BootstrapAdmin:Account"];
        string? email = configuration["BootstrapAdmin:Email"];
        string? password = configuration["BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (await userManager.FindByNameAsync(account) is not null)
        {
            return;
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = account.Trim(),
            Email = email.Trim(),
            EmailConfirmed = true,
            IsEnabled = true
        };
        IdentityResult created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException($"Bootstrap Admin 建立失敗：{string.Join(" ", created.Errors.Select(x => x.Description))}");
        }

        IdentityResult roleAdded = await userManager.AddToRoleAsync(user, SystemRoles.Admin);
        if (!roleAdded.Succeeded)
        {
            await userManager.DeleteAsync(user);
            throw new InvalidOperationException("Bootstrap Admin 無法取得 Admin 角色。");
        }

        db.UserPreferences.Add(new UserPreference(user.Id));
        await db.SaveChangesAsync(cancellationToken);
    }
}
