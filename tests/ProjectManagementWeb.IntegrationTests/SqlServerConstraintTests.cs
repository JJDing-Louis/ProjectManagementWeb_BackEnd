using System.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Domain.Enums;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;
using ProjectManagementWeb.Infrastructure.Services;

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

    [Test]
    public async Task 業務編號應跨Utc日重置且Project與Task互不影響()
    {
        DbContextOptions<ApplicationDbContext> options = GetSqlServerOptionsOrIgnore();
        DateOnly firstDate = new(2098, 1, 2);
        DateOnly secondDate = new(2098, 1, 3);

        try
        {
            await DeleteCountersAsync(options, firstDate, secondDate);

            string projectFirst = await GenerateAndCommitAsync(options, BusinessCodeType.Project,
                new DateTimeOffset(2098, 1, 2, 23, 59, 59, TimeSpan.Zero));
            string taskFirst = await GenerateAndCommitAsync(options, BusinessCodeType.Task,
                new DateTimeOffset(2098, 1, 2, 23, 59, 59, TimeSpan.Zero));
            string projectNextDay = await GenerateAndCommitAsync(options, BusinessCodeType.Project,
                new DateTimeOffset(2098, 1, 3, 0, 0, 0, TimeSpan.Zero));

            projectFirst.Should().Be("PRJ-20980102000001");
            taskFirst.Should().Be("TASK-20980102000001");
            projectNextDay.Should().Be("PRJ-20980103000001");
        }
        finally
        {
            await DeleteCountersAsync(options, firstDate, secondDate);
        }
    }

    [Test]
    public async Task 多個並行交易不應產生重複業務編號()
    {
        DbContextOptions<ApplicationDbContext> options = GetSqlServerOptionsOrIgnore();
        DateOnly businessDate = new(2098, 1, 1);

        try
        {
            await DeleteCountersAsync(options, businessDate);
            Task<string>[] requests = Enumerable.Range(0, 10)
                .Select(_ => GenerateAndCommitAsync(options, BusinessCodeType.Project,
                    new DateTimeOffset(2098, 1, 1, 0, 0, 0, TimeSpan.Zero)))
                .ToArray();

            string[] codes = await Task.WhenAll(requests);

            codes.Should().OnlyHaveUniqueItems();
            codes.Should().HaveCount(10);
            codes.Should().Contain("PRJ-20980101000001");
            codes.Should().Contain("PRJ-20980101000010");
        }
        finally
        {
            await DeleteCountersAsync(options, businessDate);
        }
    }

    [Test]
    public async Task 建立交易Rollback時不應消耗業務編號()
    {
        DbContextOptions<ApplicationDbContext> options = GetSqlServerOptionsOrIgnore();
        DateOnly businessDate = new(2098, 1, 4);

        try
        {
            await DeleteCountersAsync(options, businessDate);
            await using (var db = new ApplicationDbContext(options))
            await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
            {
                var generator = new BusinessCodeGenerator(db);
                string? rolledBackCode = await generator.GenerateAsync(BusinessCodeType.Project,
                    new DateTimeOffset(2098, 1, 4, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);
                await db.SaveChangesAsync();
                rolledBackCode.Should().Be("PRJ-20980104000001");
                await transaction.RollbackAsync();
            }

            string committedCode = await GenerateAndCommitAsync(options, BusinessCodeType.Project,
                new DateTimeOffset(2098, 1, 4, 0, 0, 0, TimeSpan.Zero));

            committedCode.Should().Be("PRJ-20980104000001");
        }
        finally
        {
            await DeleteCountersAsync(options, businessDate);
        }
    }

    [Test]
    public async Task 每日流水號達上限時應停止產生編號()
    {
        DbContextOptions<ApplicationDbContext> options = GetSqlServerOptionsOrIgnore();
        DateOnly businessDate = new(2098, 1, 5);

        try
        {
            await DeleteCountersAsync(options, businessDate);
            await using var db = new ApplicationDbContext(options);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [BusinessCodeCounters] ([CodeType], [BusinessDate], [LastValue]) VALUES ({nameof(BusinessCodeType.Task)}, {businessDate}, {999999})");
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var generator = new BusinessCodeGenerator(db);

            string? code = await generator.GenerateAsync(BusinessCodeType.Task,
                new DateTimeOffset(2098, 1, 5, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

            code.Should().BeNull();
            await transaction.RollbackAsync();
        }
        finally
        {
            await DeleteCountersAsync(options, businessDate);
        }
    }

    private static DbContextOptions<ApplicationDbContext> GetSqlServerOptionsOrIgnore()
    {
        string? connectionString = Environment.GetEnvironmentVariable("PMW_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Ignore("未設定 PMW_TEST_SQL_CONNECTION，略過實際 SQL Server constraint 測試。");
        }

        return new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connectionString).Options;
    }

    private static async Task<string> GenerateAndCommitAsync(DbContextOptions<ApplicationDbContext> options,
        BusinessCodeType codeType, DateTimeOffset now)
    {
        await using var db = new ApplicationDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var generator = new BusinessCodeGenerator(db);
        string? code = await generator.GenerateAsync(codeType, now, CancellationToken.None);
        code.Should().NotBeNull();
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return code!;
    }

    private static async Task DeleteCountersAsync(DbContextOptions<ApplicationDbContext> options,
        params DateOnly[] businessDates)
    {
        await using var db = new ApplicationDbContext(options);
        await db.Set<BusinessCodeCounter>()
            .Where(x => businessDates.Contains(x.BusinessDate))
            .ExecuteDeleteAsync();
    }
}
