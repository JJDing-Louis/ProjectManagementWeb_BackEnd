using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.UnitTests;

public sealed class DatabaseModelTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=localhost;Database=ModelOnly;Integrated Security=True;TrustServerCertificate=True")
            .Options;
        return new ApplicationDbContext(options);
    }

    [Test]
    public void AccountRole應由資料庫唯一索引限制每個帳號只有一個系統角色()
    {
        using ApplicationDbContext context = CreateContext();
        IEntityType entity = context.Model.FindEntityType(typeof(ApplicationUserRole))!;

        entity.GetIndexes().Should().Contain(index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(ApplicationUserRole.UserId) }));
    }

    [Test]
    public void ProjectMemberRole應使用三欄複合主鍵以支援一位成員多個專案角色()
    {
        using ApplicationDbContext context = CreateContext();
        IKey primaryKey = context.Model.FindEntityType(typeof(ProjectMemberRole))!.FindPrimaryKey()!;

        primaryKey.Properties.Select(property => property.Name).Should().Equal(
            nameof(ProjectMemberRole.ProjectId), nameof(ProjectMemberRole.AccountId), nameof(ProjectMemberRole.ProjectRoleId));
    }

    [Test]
    public void BusinessCodeCounter應以類型與Utc日期組成複合主鍵()
    {
        using ApplicationDbContext context = CreateContext();
        IKey primaryKey = context.Model.FindEntityType(typeof(BusinessCodeCounter))!.FindPrimaryKey()!;

        primaryKey.Properties.Select(property => property.Name).Should().Equal(
            nameof(BusinessCodeCounter.CodeType), nameof(BusinessCodeCounter.BusinessDate));
    }

    [Test]
    public void Project版本欄位應以一作為預設值()
    {
        using ApplicationDbContext context = CreateContext();
        IProperty versionProperty = context.Model.FindEntityType(typeof(Project))!
            .FindProperty(nameof(Project.VersionNumber))!;

        versionProperty.GetDefaultValue().Should().Be(1);
    }

    // 測試案例：TC-ERR-AUTH-010、TC-ERR-AUTH-015、TC-ERR-AUTH-018（Token 與重寄限制持久化契約）
    // 測試結果：Passed（Token hash／單一有效 Token／帳號與 IP 滾動查詢索引）
    // 上次測試時間：2026-09-15 22:44:47 +08:00
    [Test]
    public void Email驗證資料應以雜湊唯一索引與帳號Ip時間索引保存()
    {
        using ApplicationDbContext context = CreateContext();
        IEntityType tokenEntity = context.Model.FindEntityType(typeof(EmailVerificationToken))!;
        IEntityType attemptEntity = context.Model.FindEntityType(typeof(EmailVerificationResendAttempt))!;

        tokenEntity.FindProperty(nameof(EmailVerificationToken.TokenHash))!.GetMaxLength().Should().Be(64);
        tokenEntity.GetIndexes().Should().Contain(index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(EmailVerificationToken.TokenHash) }));
        tokenEntity.GetIndexes().Should().Contain(index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(EmailVerificationToken.AccountId) }) &&
            index.GetFilter() == "[ActivatedAt] IS NOT NULL AND [UsedAt] IS NULL AND [InvalidatedAt] IS NULL");
        attemptEntity.GetIndexes().Should().Contain(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(EmailVerificationResendAttempt.ClientAddressHash), nameof(EmailVerificationResendAttempt.RequestedAt) }));
        attemptEntity.GetIndexes().Should().Contain(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
                new[]
                {
                    nameof(EmailVerificationResendAttempt.AccountId),
                    nameof(EmailVerificationResendAttempt.Outcome),
                    nameof(EmailVerificationResendAttempt.RequestedAt)
                }));
    }

    // 測試案例：TC-SEC-AUTH-019、TC-SQL-007、TC-SQL-008（登入稽核、Email 唯一性與 Token self-FK model 契約）
    // 測試結果：Passed
    // 上次測試時間：2026-09-16 15:20:57 +08:00
    [Test]
    public void Auth資料模型應保存共享限流並強制Email唯一與RefreshReplacement參照()
    {
        using ApplicationDbContext context = CreateContext();
        IEntityType accountEntity = context.Model.FindEntityType(typeof(ApplicationUser))!;
        IEntityType loginAttemptEntity = context.Model.FindEntityType(typeof(LoginFailureAttempt))!;
        IEntityType refreshTokenEntity = context.Model.FindEntityType(typeof(RefreshToken))!;

        accountEntity.FindProperty(nameof(ApplicationUser.NormalizedEmail))!.IsNullable.Should().BeFalse();
        accountEntity.GetIndexes().Should().Contain(index =>
            index.IsUnique &&
            index.GetFilter() == null &&
            index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(ApplicationUser.NormalizedEmail) }));
        loginAttemptEntity.GetIndexes().Should().Contain(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
                new[]
                {
                    nameof(LoginFailureAttempt.AccountKeyHash),
                    nameof(LoginFailureAttempt.Outcome),
                    nameof(LoginFailureAttempt.OccurredAt)
                }));
        loginAttemptEntity.GetIndexes().Should().Contain(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
                new[]
                {
                    nameof(LoginFailureAttempt.ClientAddressHash),
                    nameof(LoginFailureAttempt.Outcome),
                    nameof(LoginFailureAttempt.OccurredAt)
                }));
        refreshTokenEntity.GetForeignKeys().Should().Contain(foreignKey =>
            foreignKey.PrincipalEntityType == refreshTokenEntity &&
            foreignKey.DeleteBehavior == DeleteBehavior.NoAction &&
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(RefreshToken.ReplacedByTokenId) }));
    }

    // 測試案例：TC-SQL-003（EF model query-filter contract；實際 SQL 查詢仍待整合測試）
    // 測試結果：Passed（EF model 層）
    // 上次測試時間：2026-09-15 15:06:33 +08:00
    [Test]
    public void 三種軟刪除實體都應設定QueryFilter()
    {
        using ApplicationDbContext context = CreateContext();

        context.Model.FindEntityType(typeof(Project))!.GetDeclaredQueryFilters().Should().NotBeEmpty();
        context.Model.FindEntityType(typeof(TaskItem))!.GetDeclaredQueryFilters().Should().NotBeEmpty();
        context.Model.FindEntityType(typeof(TaskItemComment))!.GetDeclaredQueryFilters().Should().NotBeEmpty();
    }

    // 測試案例：TC-SQL-005（EF model rowversion contract；Base64/API round-trip 仍待整合測試）
    // 測試結果：Passed（EF model 層）
    // 上次測試時間：2026-09-15 15:06:33 +08:00
    [Test]
    public void 三種並行實體都應使用SQLServerRowVersion()
    {
        using ApplicationDbContext context = CreateContext();

        foreach (Type entityType in new[] { typeof(Project), typeof(TaskItem), typeof(TaskItemComment) })
        {
            IProperty rowVersion = context.Model.FindEntityType(entityType)!.FindProperty("RowVersion")!;
            rowVersion.IsConcurrencyToken.Should().BeTrue($"{entityType.Name}.RowVersion 必須防止靜默覆蓋");
            rowVersion.ValueGenerated.Should().Be(ValueGenerated.OnAddOrUpdate);
        }
    }

    // 測試案例：TC-F-API-002（固定 System Role、Project Role 與 role-function seed）
    // 測試結果：Passed（seed contract）
    // 上次測試時間：2026-09-15 15:06:33 +08:00
    [Test]
    public void 固定角色與FunctionMapping應符合規格()
    {
        using ApplicationDbContext context = CreateContext();
        IModel designTimeModel = context.GetService<IDesignTimeModel>().Model;
        IReadOnlyList<IDictionary<string, object?>> roleSeeds = designTimeModel
            .FindEntityType(typeof(ApplicationRole))!.GetSeedData().ToList();
        IReadOnlyList<IDictionary<string, object?>> projectRoleSeeds = designTimeModel
            .FindEntityType(typeof(ProjectRole))!.GetSeedData().ToList();
        IReadOnlyList<IDictionary<string, object?>> functionSeeds = designTimeModel
            .FindEntityType(typeof(FunctionPermission))!.GetSeedData().ToList();
        IReadOnlyList<IDictionary<string, object?>> mappingSeeds = designTimeModel
            .FindEntityType(typeof(RoleFunction))!.GetSeedData().ToList();

        string[] systemRoles = roleSeeds.Select(seed => (string)seed[nameof(ApplicationRole.Name)]!).ToArray();
        systemRoles.Should().BeEquivalentTo(
            SystemRoles.Admin, SystemRoles.Administrator, SystemRoles.User, SystemRoles.Viewer);

        string[] projectRoles = projectRoleSeeds.Select(seed => (string)seed[nameof(ProjectRole.Code)]!).ToArray();
        projectRoles.Should().BeEquivalentTo(
            ProjectRoleCodes.ProjectManager,
            ProjectRoleCodes.FrontendDeveloper,
            ProjectRoleCodes.BackendDeveloper,
            ProjectRoleCodes.SystemAnalyst,
            ProjectRoleCodes.Member);

        var roleIds = roleSeeds.ToDictionary(
            seed => (string)seed[nameof(ApplicationRole.Name)]!,
            seed => (Guid)seed[nameof(ApplicationRole.Id)]!);
        var functionIds = functionSeeds.ToDictionary(
            seed => (Guid)seed[nameof(FunctionPermission.Id)]!,
            seed => (string)seed[nameof(FunctionPermission.Code)]!);
        string[] adminFunctions = mappingSeeds
            .Where(seed => (Guid)seed[nameof(RoleFunction.RoleId)]! == roleIds[SystemRoles.Admin])
            .Select(seed => functionIds[(Guid)seed[nameof(RoleFunction.FunctionId)]!])
            .ToArray();
        string[] viewerFunctions = mappingSeeds
            .Where(seed => (Guid)seed[nameof(RoleFunction.RoleId)]! == roleIds[SystemRoles.Viewer])
            .Select(seed => functionIds[(Guid)seed[nameof(RoleFunction.FunctionId)]!])
            .ToArray();

        adminFunctions.Should().BeEquivalentTo(SystemFunctions.All);
        viewerFunctions.Should().BeEquivalentTo(
            SystemFunctions.ProjectsRead,
            SystemFunctions.TasksRead,
            SystemFunctions.CommentsRead,
            SystemFunctions.PreferencesReadOwn);
    }
}
