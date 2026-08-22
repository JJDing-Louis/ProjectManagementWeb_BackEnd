using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.IntegrationTests;

public sealed class SqlServerConstraintTests
{
    [Test]
    public async Task AccountRoles應在實際SqlServer拒絕同帳號第二個系統角色()
    {
        string? connectionString = Environment.GetEnvironmentVariable("PMW_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Ignore("未設定 PMW_TEST_SQL_CONNECTION，略過實際 SQL Server constraint 測試。");
        }

        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new ApplicationDbContext(options);
        Guid userId = Guid.NewGuid();
        Guid firstRoleId = Guid.NewGuid();
        Guid secondRoleId = Guid.NewGuid();
        string suffix = Guid.NewGuid().ToString("N");

        try
        {
            db.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = $"constraint-{suffix}",
                NormalizedUserName = $"CONSTRAINT-{suffix.ToUpperInvariant()}",
                Email = $"{suffix}@example.test",
                NormalizedEmail = $"{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
                SecurityStamp = suffix,
                ConcurrencyStamp = suffix
            });
            db.Roles.AddRange(
                new ApplicationRole { Id = firstRoleId, Name = $"RoleA-{suffix}", NormalizedName = $"ROLEA-{suffix.ToUpperInvariant()}" },
                new ApplicationRole { Id = secondRoleId, Name = $"RoleB-{suffix}", NormalizedName = $"ROLEB-{suffix.ToUpperInvariant()}" });
            db.Set<ApplicationUserRole>().Add(new ApplicationUserRole { UserId = userId, RoleId = firstRoleId });
            await db.SaveChangesAsync();

            db.Set<ApplicationUserRole>().Add(new ApplicationUserRole { UserId = userId, RoleId = secondRoleId });
            Func<Task> action = () => db.SaveChangesAsync();

            await action.Should().ThrowAsync<DbUpdateException>();
        }
        finally
        {
            db.ChangeTracker.Clear();
            await db.Set<ApplicationUserRole>().Where(x => x.UserId == userId).ExecuteDeleteAsync();
            await db.Users.Where(x => x.Id == userId).ExecuteDeleteAsync();
            await db.Roles.Where(x => x.Id == firstRoleId || x.Id == secondRoleId).ExecuteDeleteAsync();
        }
    }
}
