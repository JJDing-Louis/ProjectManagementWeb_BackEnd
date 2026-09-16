using System.Data;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Domain.Enums;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;
using ProjectManagementWeb.Infrastructure.Services;

namespace ProjectManagementWeb.IntegrationTests;

public sealed class SqlServerConstraintTests
{
    // 測試案例：TC-SQL-001（實際 SQL Server constraint）
    // 測試結果：Passed（實際 SQL Server）
    // 上次測試時間：2026-09-15 15:20:55 +08:00
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

    // 測試案例：TC-SQL-002（以下四個互補情境共同覆蓋日切、隔離、並行、rollback 與上限）
    // 測試結果：Passed（4 tests；實際 SQL Server）
    // 上次測試時間：2026-09-15 15:20:55 +08:00
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

    // 測試案例：TC-SQL-003（三種軟刪除實體的實際 SQL query filter 與關聯保存）
    // 測試結果：Passed（實際 SQL Server）
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 三種軟刪除實體應由一般查詢排除但稽核查詢與歷史仍保留()
    {
        DbContextOptions<ApplicationDbContext> options = GetSqlServerOptionsOrIgnore();
        RelationalFixture fixture = await SeedRelationalFixtureAsync(options);

        try
        {
            await using (var db = new ApplicationDbContext(options))
            {
                Project project = await db.Projects.SingleAsync(x => x.Id == fixture.ProjectId);
                TaskItem task = await db.TaskItems.SingleAsync(x => x.Id == fixture.TaskId);
                TaskItemComment comment = await db.TaskItemComments.SingleAsync(x => x.Id == fixture.CommentId);
                DateTimeOffset deletedAt = DateTimeOffset.UtcNow;
                project.SoftDelete(fixture.AccountId, deletedAt);
                task.SoftDelete(deletedAt);
                comment.SoftDelete(deletedAt);
                db.TaskItemHistories.Add(new TaskItemHistory(
                    fixture.HistoryId, task.Id, fixture.AccountId, "SoftDelete", "{}", deletedAt));
                await db.SaveChangesAsync();
            }

            await using (var verify = new ApplicationDbContext(options))
            {
                (await verify.Projects.CountAsync(x => x.Id == fixture.ProjectId)).Should().Be(0);
                (await verify.TaskItems.CountAsync(x => x.Id == fixture.TaskId)).Should().Be(0);
                (await verify.TaskItemComments.CountAsync(x => x.Id == fixture.CommentId)).Should().Be(0);
                (await verify.Projects.IgnoreQueryFilters().CountAsync(x => x.Id == fixture.ProjectId)).Should().Be(1);
                (await verify.TaskItems.IgnoreQueryFilters().CountAsync(x => x.Id == fixture.TaskId)).Should().Be(1);
                (await verify.TaskItemComments.IgnoreQueryFilters().CountAsync(x => x.Id == fixture.CommentId)).Should().Be(1);
                (await verify.TaskItemHistories.CountAsync(x => x.Id == fixture.HistoryId)).Should().Be(1);
            }
        }
        finally
        {
            await CleanupRelationalFixtureAsync(options, fixture);
        }
    }

    // 測試案例：TC-SQL-005（三種實體的 SQL rowversion 自動更新與 Base64 round-trip 基礎）
    // 測試結果：Passed（實際 SQL Server）
    // 上次測試時間：2026-09-15 15:20:55 +08:00
    [Test]
    public async Task 三種並行實體每次成功更新都應產生可Base64RoundTrip的新RowVersion()
    {
        DbContextOptions<ApplicationDbContext> options = GetSqlServerOptionsOrIgnore();
        RelationalFixture fixture = await SeedRelationalFixtureAsync(options);

        try
        {
            string[] initial = await ReadRowVersionsAsync(options, fixture);
            await UpdateRelationalFixtureAsync(options, fixture, "第一次更新");
            string[] firstUpdate = await ReadRowVersionsAsync(options, fixture);
            await UpdateRelationalFixtureAsync(options, fixture, "第二次更新");
            string[] secondUpdate = await ReadRowVersionsAsync(options, fixture);

            for (int index = 0; index < initial.Length; index++)
            {
                firstUpdate[index].Should().NotBe(initial[index]);
                secondUpdate[index].Should().NotBe(firstUpdate[index]);
                Convert.ToBase64String(Convert.FromBase64String(secondUpdate[index])).Should().Be(secondUpdate[index]);
            }
        }
        finally
        {
            await CleanupRelationalFixtureAsync(options, fixture);
        }
    }

    // 測試案例：TC-SQL-004（Account、Project、Task、membership 的 FK 與刪除行為）
    // 測試結果：Passed（實際 SQL Server）
    // 上次測試時間：2026-09-16 15:47:48 +08:00
    [Test]
    public async Task 關聯資料應以Restrict或NoAction保存歷史且Membership僅Cascade角色()
    {
        DbContextOptions<ApplicationDbContext> options = GetSqlServerOptionsOrIgnore();
        RelationalFixture fixture = await SeedRelationalFixtureAsync(options);
        Guid deletedByAccountId = Guid.NewGuid();
        Guid auditId = Guid.NewGuid();
        string suffix = Guid.NewGuid().ToString("N");

        try
        {
            await using (var seed = new ApplicationDbContext(options))
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                seed.Users.Add(CreateSqlConstraintUser(
                    deletedByAccountId,
                    $"deleted-by-{suffix}",
                    $"DELETED-BY-{suffix}@EXAMPLE.TEST"));
                Guid projectManagerRoleId = await seed.ProjectRoles
                    .Where(x => x.Code == ProjectRoleCodes.ProjectManager)
                    .Select(x => x.Id)
                    .SingleAsync();
                seed.ProjectMembers.Add(new ProjectMember(fixture.ProjectId, fixture.AccountId, now));
                seed.ProjectMemberRoles.Add(new ProjectMemberRole(
                    fixture.ProjectId,
                    fixture.AccountId,
                    projectManagerRoleId));
                seed.TaskItemHistories.Add(new TaskItemHistory(
                    fixture.HistoryId,
                    fixture.TaskId,
                    fixture.AccountId,
                    "Update",
                    "{}",
                    now));
                seed.AuditLogs.Add(new AuditLog(
                    auditId,
                    fixture.AccountId,
                    "Update",
                    "Project",
                    fixture.ProjectId.ToString(),
                    "{}",
                    "{}",
                    now));
                Project project = await seed.Projects.SingleAsync(x => x.Id == fixture.ProjectId);
                project.SoftDelete(deletedByAccountId, now);
                await seed.SaveChangesAsync();
            }

            await AssertDeleteFailsAsync(options, async db =>
                db.Users.Remove(await db.Users.SingleAsync(x => x.Id == fixture.AccountId)));
            await AssertDeleteFailsAsync(options, async db =>
                db.Users.Remove(await db.Users.SingleAsync(x => x.Id == deletedByAccountId)));

            await using (var membershipDb = new ApplicationDbContext(options))
            {
                ProjectMember membership = await membershipDb.ProjectMembers.SingleAsync(x =>
                    x.ProjectId == fixture.ProjectId && x.AccountId == fixture.AccountId);
                membershipDb.ProjectMembers.Remove(membership);
                await membershipDb.SaveChangesAsync();
            }

            await using (var verifyCascade = new ApplicationDbContext(options))
            {
                (await verifyCascade.ProjectMembers.CountAsync(x =>
                    x.ProjectId == fixture.ProjectId && x.AccountId == fixture.AccountId)).Should().Be(0);
                (await verifyCascade.ProjectMemberRoles.CountAsync(x =>
                    x.ProjectId == fixture.ProjectId && x.AccountId == fixture.AccountId)).Should().Be(0);
                (await verifyCascade.Projects.IgnoreQueryFilters().CountAsync(x => x.Id == fixture.ProjectId))
                    .Should().Be(1);
                (await verifyCascade.TaskItems.CountAsync(x => x.Id == fixture.TaskId)).Should().Be(1);
            }

            await AssertDeleteFailsAsync(options, async db =>
                db.Projects.Remove(await db.Projects.IgnoreQueryFilters().SingleAsync(x => x.Id == fixture.ProjectId)));
            await AssertDeleteFailsAsync(options, async db =>
                db.TaskItems.Remove(await db.TaskItems.SingleAsync(x => x.Id == fixture.TaskId)));

            await using var verify = new ApplicationDbContext(options);
            (await verify.Users.CountAsync(x => x.Id == fixture.AccountId || x.Id == deletedByAccountId))
                .Should().Be(2);
            (await verify.Projects.IgnoreQueryFilters().CountAsync(x => x.Id == fixture.ProjectId)).Should().Be(1);
            (await verify.TaskItems.CountAsync(x => x.Id == fixture.TaskId)).Should().Be(1);
            (await verify.TaskItemComments.CountAsync(x => x.Id == fixture.CommentId)).Should().Be(1);
            (await verify.TaskItemHistories.CountAsync(x => x.Id == fixture.HistoryId)).Should().Be(1);
            (await verify.AuditLogs.CountAsync(x => x.Id == auditId)).Should().Be(1);
        }
        finally
        {
            await using (var cleanup = new ApplicationDbContext(options))
            {
                await cleanup.AuditLogs.Where(x => x.Id == auditId).ExecuteDeleteAsync();
            }
            await CleanupRelationalFixtureAsync(options, fixture);
            await using var accountCleanup = new ApplicationDbContext(options);
            await accountCleanup.Users.Where(x => x.Id == deletedByAccountId).ExecuteDeleteAsync();
        }
    }

    // 測試案例：TC-SQL-007（NormalizedEmail NOT NULL 與無 filter UNIQUE）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:20:57 +08:00
    [Test]
    public async Task Accounts應在實際SqlServer拒絕Null與重複NormalizedEmail()
    {
        DbContextOptions<ApplicationDbContext> options = GetSqlServerOptionsOrIgnore();
        Guid firstId = Guid.NewGuid();
        Guid duplicateId = Guid.NewGuid();
        Guid nullId = Guid.NewGuid();
        string suffix = Guid.NewGuid().ToString("N");
        string normalizedEmail = $"UNIQUE-{suffix}@EXAMPLE.TEST";

        try
        {
            await using (var db = new ApplicationDbContext(options))
            {
                db.Users.Add(CreateSqlConstraintUser(firstId, $"email-a-{suffix}", normalizedEmail));
                await db.SaveChangesAsync();
            }

            await using (var duplicateDb = new ApplicationDbContext(options))
            {
                duplicateDb.Users.Add(CreateSqlConstraintUser(duplicateId, $"email-b-{suffix}", normalizedEmail));
                Func<Task> saveDuplicate = () => duplicateDb.SaveChangesAsync();
                await saveDuplicate.Should().ThrowAsync<DbUpdateException>();
            }

            await using (var nullDb = new ApplicationDbContext(options))
            {
                ApplicationUser nullEmail = CreateSqlConstraintUser(nullId, $"email-null-{suffix}", normalizedEmail);
                nullEmail.NormalizedEmail = null;
                nullDb.Users.Add(nullEmail);
                Func<Task> saveNull = () => nullDb.SaveChangesAsync();
                await saveNull.Should().ThrowAsync<DbUpdateException>();
            }
        }
        finally
        {
            await using var cleanup = new ApplicationDbContext(options);
            await cleanup.Users.Where(x => x.Id == firstId || x.Id == duplicateId || x.Id == nullId).ExecuteDeleteAsync();
        }
    }

    // 測試案例：TC-SQL-007（migration 髒資料 fail-fast、人工修正後續跑）
    // 測試結果：Passed（使用測試專用臨時資料庫）
    // 上次測試時間：2026-09-16 15:20:57 +08:00
    [Test]
    public async Task NormalizedEmailMigration遇Null與重複資料應FailFast且人工修正後可重跑()
    {
        string? sourceConnectionString = Environment.GetEnvironmentVariable("PMW_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(sourceConnectionString))
        {
            Assert.Ignore("未設定 PMW_TEST_SQL_CONNECTION，略過實際 SQL Server migration 測試。");
        }

        string databaseName = $"PMW_MigrationTest_{Guid.NewGuid():N}";
        var testBuilder = new SqlConnectionStringBuilder(sourceConnectionString)
        {
            InitialCatalog = databaseName
        };
        var masterBuilder = new SqlConnectionStringBuilder(sourceConnectionString)
        {
            InitialCatalog = "master"
        };

        await ExecuteMasterCommandAsync(masterBuilder.ConnectionString, $"CREATE DATABASE [{databaseName}]");
        try
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(testBuilder.ConnectionString)
                .Options;
            string migrationBeforeIntegrity;
            string latestMigration;
            await using (var db = new ApplicationDbContext(options))
            {
                string[] migrations = db.Database.GetMigrations().ToArray();
                migrationBeforeIntegrity = migrations.Single(x => x.EndsWith("_AddLoginFailureRateLimits", StringComparison.Ordinal));
                latestMigration = migrations.Single(x => x.EndsWith("_EnforceEmailAndRefreshTokenIntegrity", StringComparison.Ordinal));
                await db.GetService<IMigrator>().MigrateAsync(migrationBeforeIntegrity);
                await InsertMigrationAccountAsync(db, Guid.NewGuid(), "migration-null", null);
                await InsertMigrationAccountAsync(db, Guid.NewGuid(), "migration-duplicate", "DUPLICATE@EXAMPLE.TEST");
            }

            await using (var nullDb = new ApplicationDbContext(options))
            {
                Func<Task> migrateNull = () => nullDb.GetService<IMigrator>().MigrateAsync(latestMigration);
                await migrateNull.Should().ThrowAsync<Exception>()
                    .WithMessage("*NormalizedEmail migration blocked: NULL data*");
                await nullDb.Database.ExecuteSqlRawAsync(
                    "UPDATE [Accounts] SET [NormalizedEmail] = 'DUPLICATE@EXAMPLE.TEST' WHERE [NormalizedEmail] IS NULL");
            }

            await using (var duplicateDb = new ApplicationDbContext(options))
            {
                Func<Task> migrateDuplicate = () => duplicateDb.GetService<IMigrator>().MigrateAsync(latestMigration);
                await migrateDuplicate.Should().ThrowAsync<Exception>()
                    .WithMessage("*NormalizedEmail migration blocked: duplicate data*");
                await duplicateDb.Database.ExecuteSqlRawAsync(
                    "UPDATE [Accounts] SET [NormalizedEmail] = 'UNIQUE@EXAMPLE.TEST' WHERE [UserName] = 'migration-duplicate'");
                await duplicateDb.GetService<IMigrator>().MigrateAsync(latestMigration);
                (await duplicateDb.Database.GetAppliedMigrationsAsync()).Should().Contain(latestMigration);
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteMasterCommandAsync(
                masterBuilder.ConnectionString,
                $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]");
        }
    }

    // 測試案例：TC-F-PRJ-003、TC-F-REM-001（既有 Project 時區 migration 設定與回填）
    // 測試結果：Passed（使用測試專用臨時資料庫）
    // 上次測試時間：2026-09-16 15:20:57 +08:00
    [Test]
    public async Task ProjectTimeZoneMigration應要求合法Iana設定並只回填既有資料()
    {
        string? sourceConnectionString = Environment.GetEnvironmentVariable("PMW_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(sourceConnectionString))
        {
            Assert.Ignore("未設定 PMW_TEST_SQL_CONNECTION，略過實際 SQL Server migration 測試。");
        }

        string databaseName = $"PMW_TimeZoneMigrationTest_{Guid.NewGuid():N}";
        var testBuilder = new SqlConnectionStringBuilder(sourceConnectionString) { InitialCatalog = databaseName };
        var masterBuilder = new SqlConnectionStringBuilder(sourceConnectionString) { InitialCatalog = "master" };
        string? originalDefault = Environment.GetEnvironmentVariable("PMW_MIGRATION_DEFAULT_TIME_ZONE_ID");
        Guid accountId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();

        await ExecuteMasterCommandAsync(masterBuilder.ConnectionString, $"CREATE DATABASE [{databaseName}]");
        try
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(testBuilder.ConnectionString)
                .Options;
            string priorMigration;
            string latestMigration;
            await using (var db = new ApplicationDbContext(options))
            {
                string[] migrations = db.Database.GetMigrations().ToArray();
                priorMigration = migrations.Single(x =>
                    x.EndsWith("_EnforceEmailAndRefreshTokenIntegrity", StringComparison.Ordinal));
                latestMigration = migrations.Single(x => x.EndsWith("_AddProjectTimeZone", StringComparison.Ordinal));
                await db.GetService<IMigrator>().MigrateAsync(priorMigration);
                await InsertMigrationAccountAsync(db, accountId, "timezone-owner", "TIMEZONE-OWNER@EXAMPLE.TEST");
                await db.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO [Projects]
                        ([Id], [Code], [Name], [Description], [OwnerAccountId], [Status], [VersionNumber],
                         [CreatedAt], [UpdatedAt], [DeletedAt])
                    VALUES
                        ({{projectId}}, 'PRJ-TIMEZONE-TEST', '時區回填專案', NULL, {{accountId}}, 'Pending', 1,
                         SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), NULL)
                    """);
            }

            Environment.SetEnvironmentVariable("PMW_MIGRATION_DEFAULT_TIME_ZONE_ID", null);
            await using (var missingDb = new ApplicationDbContext(options))
            {
                Func<Task> migrateWithoutSetting = () => missingDb.GetService<IMigrator>().MigrateAsync(latestMigration);
                await migrateWithoutSetting.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("*PMW_MIGRATION_DEFAULT_TIME_ZONE_ID*");
            }

            Environment.SetEnvironmentVariable("PMW_MIGRATION_DEFAULT_TIME_ZONE_ID", "Taipei Standard Time");
            await using (var invalidDb = new ApplicationDbContext(options))
            {
                Func<Task> migrateInvalidSetting = () => invalidDb.GetService<IMigrator>().MigrateAsync(latestMigration);
                await migrateInvalidSetting.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("*PMW_MIGRATION_DEFAULT_TIME_ZONE_ID*");
            }

            Environment.SetEnvironmentVariable("PMW_MIGRATION_DEFAULT_TIME_ZONE_ID", "Asia/Taipei");
            await using (var validDb = new ApplicationDbContext(options))
            {
                await validDb.GetService<IMigrator>().MigrateAsync(latestMigration);
                string timeZoneId = await validDb.Projects.Where(x => x.Id == projectId)
                    .Select(x => x.TimeZoneId)
                    .SingleAsync();
                timeZoneId.Should().Be("Asia/Taipei");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PMW_MIGRATION_DEFAULT_TIME_ZONE_ID", originalDefault);
            SqlConnection.ClearAllPools();
            await ExecuteMasterCommandAsync(
                masterBuilder.ConnectionString,
                $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]");
        }
    }

    // 測試案例：TC-SQL-008（合法 rotation、未知 replacement 與 NoAction 刪除保護）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:20:57 +08:00
    [Test]
    public async Task RefreshTokenReplacement應由實際SqlServerSelfFk保護且不得Cascade刪除()
    {
        DbContextOptions<ApplicationDbContext> options = GetSqlServerOptionsOrIgnore();
        Guid accountId = Guid.NewGuid();
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        Guid invalidId = Guid.NewGuid();
        string suffix = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        try
        {
            await using (var db = new ApplicationDbContext(options))
            {
                db.Users.Add(CreateSqlConstraintUser(accountId, $"refresh-{suffix}", $"REFRESH-{suffix}@EXAMPLE.TEST"));
                var first = new RefreshToken(firstId, accountId, Guid.NewGuid(), new string('A', 64), now, now.AddDays(7));
                var second = new RefreshToken(secondId, accountId, Guid.NewGuid(), new string('B', 64), now, now.AddDays(7));
                first.Revoke(now, secondId);
                db.RefreshTokens.AddRange(first, second);
                await db.SaveChangesAsync();
            }

            await using (var invalidDb = new ApplicationDbContext(options))
            {
                var invalid = new RefreshToken(invalidId, accountId, Guid.NewGuid(), new string('C', 64), now, now.AddDays(7));
                invalid.Revoke(now, Guid.NewGuid());
                invalidDb.RefreshTokens.Add(invalid);
                Func<Task> saveInvalid = () => invalidDb.SaveChangesAsync();
                await saveInvalid.Should().ThrowAsync<DbUpdateException>();
            }

            await using (var deleteDb = new ApplicationDbContext(options))
            {
                RefreshToken referenced = await deleteDb.RefreshTokens.SingleAsync(x => x.Id == secondId);
                deleteDb.RefreshTokens.Remove(referenced);
                Func<Task> deleteReferenced = () => deleteDb.SaveChangesAsync();
                await deleteReferenced.Should().ThrowAsync<DbUpdateException>();
            }

            await using (var verify = new ApplicationDbContext(options))
            {
                RefreshToken[] family = await verify.RefreshTokens
                    .Where(x => x.Id == firstId || x.Id == secondId)
                    .OrderBy(x => x.Id)
                    .ToArrayAsync();
                family.Should().HaveCount(2);
                family.Single(x => x.Id == firstId).ReplacedByTokenId.Should().Be(secondId);
            }
        }
        finally
        {
            await using var cleanup = new ApplicationDbContext(options);
            await cleanup.RefreshTokens.Where(x => x.Id == firstId || x.Id == invalidId).ExecuteDeleteAsync();
            await cleanup.RefreshTokens.Where(x => x.Id == secondId).ExecuteDeleteAsync();
            await cleanup.Users.Where(x => x.Id == accountId).ExecuteDeleteAsync();
        }
    }

    private static ApplicationUser CreateSqlConstraintUser(Guid id, string account, string normalizedEmail) => new()
    {
        Id = id,
        UserName = account,
        NormalizedUserName = account.ToUpperInvariant(),
        Email = normalizedEmail.ToLowerInvariant(),
        NormalizedEmail = normalizedEmail,
        Name = account,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        IsEnabled = true
    };

    private static Task InsertMigrationAccountAsync(
        ApplicationDbContext db,
        Guid id,
        string account,
        string? normalizedEmail) =>
        db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO [Accounts]
                ([Id], [UserName], [NormalizedUserName], [Email], [NormalizedEmail], [EmailConfirmed],
                 [SecurityStamp], [ConcurrencyStamp], [PhoneNumberConfirmed], [TwoFactorEnabled],
                 [LockoutEnabled], [AccessFailedCount], [Name], [IsEnabled], [TokenVersion])
            VALUES
                ({{id}}, {{account}}, {{account.ToUpperInvariant()}}, {{account + "@example.test"}}, {{normalizedEmail}}, 0,
                 {{Guid.NewGuid().ToString("N")}}, {{Guid.NewGuid().ToString("N")}}, 0, 0, 0, 0, {{account}}, 1, 0)
            """);

    private static async Task ExecuteMasterCommandAsync(string connectionString, string commandText)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
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

    private static async Task AssertDeleteFailsAsync(
        DbContextOptions<ApplicationDbContext> options,
        Func<ApplicationDbContext, Task> arrangeDelete)
    {
        await using var db = new ApplicationDbContext(options);
        await arrangeDelete(db);
        Func<Task> save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    private static async Task<RelationalFixture> SeedRelationalFixtureAsync(
        DbContextOptions<ApplicationDbContext> options)
    {
        var fixture = new RelationalFixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        string suffix = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var db = new ApplicationDbContext(options);
        db.Users.Add(new ApplicationUser
        {
            Id = fixture.AccountId,
            UserName = $"relational-{suffix}",
            NormalizedUserName = $"RELATIONAL-{suffix.ToUpperInvariant()}",
            Email = $"{suffix}@example.test",
            NormalizedEmail = $"{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            SecurityStamp = suffix,
            ConcurrencyStamp = suffix,
            IsEnabled = true
        });
        db.Projects.Add(new Project(fixture.ProjectId, $"PRJ-R{suffix}"[..20], "SQL 關聯測試", null,
            fixture.AccountId, "Asia/Taipei", now));
        db.TaskItems.Add(new TaskItem(fixture.TaskId, $"TASK-R{suffix}"[..22], fixture.ProjectId,
            fixture.AccountId, fixture.AccountId, "SQL Task", null, now, now.AddDays(1), now));
        db.TaskItemComments.Add(new TaskItemComment(fixture.CommentId, fixture.TaskId, fixture.AccountId,
            "SQL Comment", now));
        await db.SaveChangesAsync();
        return fixture;
    }

    private static async Task<string[]> ReadRowVersionsAsync(
        DbContextOptions<ApplicationDbContext> options,
        RelationalFixture fixture)
    {
        await using var db = new ApplicationDbContext(options);
        return
        [
            Convert.ToBase64String(await db.Projects.Where(x => x.Id == fixture.ProjectId)
                .Select(x => x.RowVersion).SingleAsync()),
            Convert.ToBase64String(await db.TaskItems.Where(x => x.Id == fixture.TaskId)
                .Select(x => x.RowVersion).SingleAsync()),
            Convert.ToBase64String(await db.TaskItemComments.Where(x => x.Id == fixture.CommentId)
                .Select(x => x.RowVersion).SingleAsync())
        ];
    }

    private static async Task UpdateRelationalFixtureAsync(
        DbContextOptions<ApplicationDbContext> options,
        RelationalFixture fixture,
        string marker)
    {
        await using var db = new ApplicationDbContext(options);
        Project project = await db.Projects.SingleAsync(x => x.Id == fixture.ProjectId);
        TaskItem task = await db.TaskItems.SingleAsync(x => x.Id == fixture.TaskId);
        TaskItemComment comment = await db.TaskItemComments.SingleAsync(x => x.Id == fixture.CommentId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        project.Update(marker, marker, fixture.AccountId, "Asia/Taipei", ProjectStatus.Active, now);
        task.UpdateStatus(ProjectManagementWeb.Domain.Enums.TaskStatus.InProgress, now);
        comment.Update(marker, now);
        await db.SaveChangesAsync();
    }

    private static async Task CleanupRelationalFixtureAsync(
        DbContextOptions<ApplicationDbContext> options,
        RelationalFixture fixture)
    {
        await using var db = new ApplicationDbContext(options);
        await db.TaskItemHistories.Where(x => x.TaskItemId == fixture.TaskId).ExecuteDeleteAsync();
        await db.TaskItemComments.IgnoreQueryFilters().Where(x => x.Id == fixture.CommentId).ExecuteDeleteAsync();
        await db.TaskItems.IgnoreQueryFilters().Where(x => x.Id == fixture.TaskId).ExecuteDeleteAsync();
        await db.Projects.IgnoreQueryFilters().Where(x => x.Id == fixture.ProjectId).ExecuteDeleteAsync();
        await db.Users.Where(x => x.Id == fixture.AccountId).ExecuteDeleteAsync();
    }

    private sealed record RelationalFixture(
        Guid AccountId,
        Guid ProjectId,
        Guid TaskId,
        Guid CommentId,
        Guid HistoryId);
}
