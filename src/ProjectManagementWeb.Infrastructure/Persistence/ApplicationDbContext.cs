using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Infrastructure.Identity;

namespace ProjectManagementWeb.Infrastructure.Persistence;

public sealed class ApplicationDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid,
    IdentityUserClaim<Guid>, ApplicationUserRole, IdentityUserLogin<Guid>, IdentityRoleClaim<Guid>, IdentityUserToken<Guid>>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<FunctionPermission> Functions => Set<FunctionPermission>();
    public DbSet<RoleFunction> RoleFunctions => Set<RoleFunction>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<ProjectRole> ProjectRoles => Set<ProjectRole>();
    public DbSet<ProjectMemberRole> ProjectMemberRoles => Set<ProjectMemberRole>();
    public DbSet<TaskItem> TaskItems => Set<TaskItem>();
    public DbSet<TaskItemComment> TaskItemComments => Set<TaskItemComment>();
    public DbSet<TaskItemHistory> TaskItemHistories => Set<TaskItemHistory>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();
    public DbSet<BusinessCodeCounter> BusinessCodeCounters => Set<BusinessCodeCounter>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        ConfigureIdentity(builder);
        ConfigureRbac(builder);
        ConfigureProjects(builder);
        ConfigureTasks(builder);
        ConfigureSystemTables(builder);
        SeedStaticData(builder);
    }

    private static void ConfigureIdentity(ModelBuilder builder)
    {
        builder.Entity<ApplicationUser>(entity =>
        {
            entity.ToTable("Accounts");
            entity.Property(x => x.Name).HasMaxLength(100);
            entity.Property(x => x.Remark).HasMaxLength(500);
        });
        builder.Entity<ApplicationRole>().ToTable("Roles");
        builder.Entity<ApplicationUserRole>(entity =>
        {
            entity.ToTable("AccountRoles");
            entity.HasIndex(x => x.UserId).IsUnique();
        });
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("AccountClaims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("AccountLogins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("AccountTokens");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("RoleClaims");
    }

    private static void ConfigureRbac(ModelBuilder builder)
    {
        builder.Entity<FunctionPermission>(entity =>
        {
            entity.ToTable("Functions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.Code).IsUnique();
        });
        builder.Entity<RoleFunction>(entity =>
        {
            entity.ToTable("RoleFunctions");
            entity.HasKey(x => new { x.RoleId, x.FunctionId });
            entity.HasOne<ApplicationRole>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<FunctionPermission>().WithMany().HasForeignKey(x => x.FunctionId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.AccountId, x.FamilyId });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<UserPreference>(entity =>
        {
            entity.ToTable("UserPreferences");
            entity.HasKey(x => x.AccountId);
            entity.HasOne<ApplicationUser>().WithOne().HasForeignKey<UserPreference>(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureProjects(ModelBuilder builder)
    {
        builder.Entity<Project>(entity =>
        {
            entity.ToTable("Projects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(4000);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasIndex(x => x.Code).IsUnique().HasFilter("[DeletedAt] IS NULL");
            entity.HasIndex(x => new { x.OwnerAccountId, x.Status });
            entity.HasQueryFilter(x => x.DeletedAt == null);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ProjectMember>(entity =>
        {
            entity.ToTable("ProjectMembers");
            entity.HasKey(x => new { x.ProjectId, x.AccountId });
            entity.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ProjectRole>(entity =>
        {
            entity.ToTable("ProjectRoles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.Code).IsUnique();
        });
        builder.Entity<ProjectMemberRole>(entity =>
        {
            entity.ToTable("ProjectMemberRoles");
            entity.HasKey(x => new { x.ProjectId, x.AccountId, x.ProjectRoleId });
            entity.HasOne<ProjectMember>().WithMany()
                .HasForeignKey(x => new { x.ProjectId, x.AccountId }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProjectRole>().WithMany().HasForeignKey(x => x.ProjectRoleId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureTasks(ModelBuilder builder)
    {
        builder.Entity<TaskItem>(entity =>
        {
            entity.ToTable("TaskItems");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(8000);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasIndex(x => x.Code).IsUnique().HasFilter("[DeletedAt] IS NULL");
            entity.HasIndex(x => new { x.ProjectId, x.Status, x.Deadline });
            entity.HasIndex(x => x.AssignedAccountId);
            entity.HasQueryFilter(x => x.DeletedAt == null);
            entity.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedByAccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AssignedAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<TaskItemComment>(entity =>
        {
            entity.ToTable("TaskItemComments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Content).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasIndex(x => new { x.TaskItemId, x.CreatedAt });
            entity.HasQueryFilter(x => x.DeletedAt == null);
            entity.HasOne<TaskItem>().WithMany().HasForeignKey(x => x.TaskItemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AuthorAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<TaskItemHistory>(entity =>
        {
            entity.ToTable("TaskItemHistories");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(30).IsRequired();
            entity.Property(x => x.Snapshot).HasColumnType("nvarchar(max)").IsRequired();
            entity.HasIndex(x => new { x.TaskItemId, x.CreatedAt });
            entity.HasOne<TaskItem>().WithMany().HasForeignKey(x => x.TaskItemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorAccountId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureSystemTables(ModelBuilder builder)
    {
        builder.Entity<BusinessCodeCounter>(entity =>
        {
            entity.ToTable("BusinessCodeCounters", table =>
            {
                table.HasCheckConstraint("CK_BusinessCodeCounters_CodeType", "[CodeType] IN ('Project', 'Task')");
                table.HasCheckConstraint("CK_BusinessCodeCounters_LastValue", "[LastValue] BETWEEN 1 AND 999999");
            });
            entity.HasKey(x => new { x.CodeType, x.BusinessDate });
            entity.Property(x => x.CodeType).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.BusinessDate).HasColumnType("date");
        });
        builder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("AuditLogs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(100).IsRequired();
            entity.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.EntityId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.BeforeData).HasColumnType("nvarchar(max)");
            entity.Property(x => x.AfterData).HasColumnType("nvarchar(max)");
            entity.HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAt });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorAccountId).OnDelete(DeleteBehavior.NoAction);
        });
        builder.Entity<EmailMessage>(entity =>
        {
            entity.ToTable("EmailMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Recipient).HasMaxLength(320).IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Body).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasIndex(x => new { x.Status, x.CreatedAt });
        });
    }

    private static void SeedStaticData(ModelBuilder builder)
    {
        var roles = new[]
        {
            new ApplicationRole { Id = SeedIds.Create($"role:{SystemRoles.Admin}"), Name = SystemRoles.Admin, NormalizedName = SystemRoles.Admin.ToUpperInvariant(), Description = "最高管理者", ConcurrencyStamp = "seed-admin" },
            new ApplicationRole { Id = SeedIds.Create($"role:{SystemRoles.Administrator}"), Name = SystemRoles.Administrator, NormalizedName = SystemRoles.Administrator.ToUpperInvariant(), Description = "後台管理員", ConcurrencyStamp = "seed-administrator" },
            new ApplicationRole { Id = SeedIds.Create($"role:{SystemRoles.User}"), Name = SystemRoles.User, NormalizedName = SystemRoles.User.ToUpperInvariant(), Description = "一般使用者", ConcurrencyStamp = "seed-user" },
            new ApplicationRole { Id = SeedIds.Create($"role:{SystemRoles.Viewer}"), Name = SystemRoles.Viewer, NormalizedName = SystemRoles.Viewer.ToUpperInvariant(), Description = "瀏覽者", ConcurrencyStamp = "seed-viewer" }
        };
        builder.Entity<ApplicationRole>().HasData(roles);

        var functions = SystemFunctions.All.Select(code => new FunctionPermission(SeedIds.Create($"function:{code}"), code, code)).ToArray();
        builder.Entity<FunctionPermission>().HasData(functions);

        IEnumerable<string> administratorFunctions =
        [
            SystemFunctions.AccountsRead, SystemFunctions.ProjectsRead, SystemFunctions.ProjectsCreate,
            SystemFunctions.ProjectsManageAll, SystemFunctions.ProjectMembersManageAll, SystemFunctions.TasksRead,
            SystemFunctions.TasksCreate, SystemFunctions.TasksUpdateAny, SystemFunctions.TasksDelete,
            SystemFunctions.CommentsRead, SystemFunctions.CommentsCreate, SystemFunctions.CommentsUpdateOwn,
            SystemFunctions.CommentsDeleteOwn, SystemFunctions.PreferencesReadOwn, SystemFunctions.PreferencesUpdateOwn
        ];
        IEnumerable<string> userFunctions =
        [
            SystemFunctions.ProjectsRead, SystemFunctions.TasksRead, SystemFunctions.TasksUpdateAssigned,
            SystemFunctions.CommentsRead, SystemFunctions.CommentsCreate, SystemFunctions.CommentsUpdateOwn,
            SystemFunctions.CommentsDeleteOwn, SystemFunctions.PreferencesReadOwn, SystemFunctions.PreferencesUpdateOwn
        ];
        IEnumerable<string> viewerFunctions =
        [SystemFunctions.ProjectsRead, SystemFunctions.TasksRead, SystemFunctions.CommentsRead, SystemFunctions.PreferencesReadOwn];

        var mappings = new List<RoleFunction>();
        mappings.AddRange(SystemFunctions.All.Select(code => new RoleFunction(roles[0].Id, SeedIds.Create($"function:{code}"))));
        mappings.AddRange(administratorFunctions.Select(code => new RoleFunction(roles[1].Id, SeedIds.Create($"function:{code}"))));
        mappings.AddRange(userFunctions.Select(code => new RoleFunction(roles[2].Id, SeedIds.Create($"function:{code}"))));
        mappings.AddRange(viewerFunctions.Select(code => new RoleFunction(roles[3].Id, SeedIds.Create($"function:{code}"))));
        builder.Entity<RoleFunction>().HasData(mappings);

        string[] projectRoleCodes =
        [ProjectRoleCodes.ProjectManager, ProjectRoleCodes.FrontendDeveloper, ProjectRoleCodes.BackendDeveloper, ProjectRoleCodes.SystemAnalyst, ProjectRoleCodes.Member];
        builder.Entity<ProjectRole>().HasData(projectRoleCodes.Select(code => new ProjectRole(SeedIds.Create($"project-role:{code}"), code, code)));
    }
}
