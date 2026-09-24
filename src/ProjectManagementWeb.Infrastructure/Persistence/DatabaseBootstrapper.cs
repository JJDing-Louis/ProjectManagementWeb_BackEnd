using System.Data;
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
        if (string.IsNullOrWhiteSpace(account))
        {
            return;
        }

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ApplicationUser? existingUser = await userManager.FindByNameAsync(account);
        if (existingUser is not null)
        {
            await EnsureAdminRoleAndEnabledAsync(userManager, db, existingUser, cancellationToken);
            return;
        }
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
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

    private static async Task EnsureAdminRoleAndEnabledAsync(
        UserManager<ApplicationUser> userManager, ApplicationDbContext db, ApplicationUser user,
        CancellationToken cancellationToken)
    {
        IList<string> currentRoles = await userManager.GetRolesAsync(user);
        bool needsAdminRole = !currentRoles.Contains(SystemRoles.Admin, StringComparer.Ordinal);
        bool needsEnabled = !user.IsEnabled;
        string[] nonAdminRoles = currentRoles
            .Where(role => !string.Equals(role, SystemRoles.Admin, StringComparison.Ordinal))
            .ToArray();
        if (nonAdminRoles.Length == 0 && !needsAdminRole && !needsEnabled)
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (nonAdminRoles.Length > 0)
        {
            IdentityResult removed = await userManager.RemoveFromRolesAsync(user, nonAdminRoles);
            if (!removed.Succeeded)
            {
                throw new InvalidOperationException("Bootstrap Admin 無法移除非 Admin 角色。");
            }
        }

        if (needsAdminRole)
        {
            IdentityResult roleAdded = await userManager.AddToRoleAsync(user, SystemRoles.Admin);
            if (!roleAdded.Succeeded)
            {
                throw new InvalidOperationException("Bootstrap Admin 無法取得 Admin 角色。");
            }
        }

        if (needsEnabled)
        {
            user.IsEnabled = true;
            IdentityResult updated = await userManager.UpdateAsync(user);
            if (!updated.Succeeded)
            {
                throw new InvalidOperationException("Bootstrap Admin 無法恢復啟用狀態。");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
