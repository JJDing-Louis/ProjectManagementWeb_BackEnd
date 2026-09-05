using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
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
}
