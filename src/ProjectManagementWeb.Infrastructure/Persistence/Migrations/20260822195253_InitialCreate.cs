using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ProjectManagementWeb.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Accounts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                TokenVersion = table.Column<int>(type: "int", nullable: false),
                Remark = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                PhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                AccessFailedCount = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Accounts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "EmailMessages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Recipient = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                AttemptCount = table.Column<int>(type: "int", nullable: false),
                LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EmailMessages", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Functions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Functions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ProjectRoles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProjectRoles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Roles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Roles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AccountClaims",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AccountClaims", x => x.Id);
                table.ForeignKey(
                    name: "FK_AccountClaims_Accounts_UserId",
                    column: x => x.UserId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AccountLogins",
            columns: table => new
            {
                LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                ProviderKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AccountLogins", x => new { x.LoginProvider, x.ProviderKey });
                table.ForeignKey(
                    name: "FK_AccountLogins_Accounts_UserId",
                    column: x => x.UserId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AccountTokens",
            columns: table => new
            {
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AccountTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                table.ForeignKey(
                    name: "FK_AccountTokens_Accounts_UserId",
                    column: x => x.UserId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AuditLogs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ActorAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                EntityId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                BeforeData = table.Column<string>(type: "nvarchar(max)", nullable: true),
                AfterData = table.Column<string>(type: "nvarchar(max)", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditLogs", x => x.Id);
                table.ForeignKey(
                    name: "FK_AuditLogs_Accounts_ActorAccountId",
                    column: x => x.ActorAccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "Projects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                OwnerAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Projects", x => x.Id);
                table.ForeignKey(
                    name: "FK_Projects_Accounts_OwnerAccountId",
                    column: x => x.OwnerAccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "RefreshTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                ReplacedByTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_RefreshTokens_Accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "UserPreferences",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SkipBatchConfirmation = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserPreferences", x => x.AccountId);
                table.ForeignKey(
                    name: "FK_UserPreferences_Accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AccountRoles",
            columns: table => new
            {
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AccountRoles", x => new { x.UserId, x.RoleId });
                table.ForeignKey(
                    name: "FK_AccountRoles_Accounts_UserId",
                    column: x => x.UserId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_AccountRoles_Roles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "Roles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RoleClaims",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RoleClaims", x => x.Id);
                table.ForeignKey(
                    name: "FK_RoleClaims_Roles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "Roles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RoleFunctions",
            columns: table => new
            {
                RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                FunctionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RoleFunctions", x => new { x.RoleId, x.FunctionId });
                table.ForeignKey(
                    name: "FK_RoleFunctions_Functions_FunctionId",
                    column: x => x.FunctionId,
                    principalTable: "Functions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_RoleFunctions_Roles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "Roles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ProjectMembers",
            columns: table => new
            {
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                JoinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProjectMembers", x => new { x.ProjectId, x.AccountId });
                table.ForeignKey(
                    name: "FK_ProjectMembers_Accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ProjectMembers_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TaskItems",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CreatedByAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AssignedAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                Description = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                StartAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                Deadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaskItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_TaskItems_Accounts_AssignedAccountId",
                    column: x => x.AssignedAccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_TaskItems_Accounts_CreatedByAccountId",
                    column: x => x.CreatedByAccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_TaskItems_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ProjectMemberRoles",
            columns: table => new
            {
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProjectMemberRoles", x => new { x.ProjectId, x.AccountId, x.ProjectRoleId });
                table.ForeignKey(
                    name: "FK_ProjectMemberRoles_ProjectMembers_ProjectId_AccountId",
                    columns: x => new { x.ProjectId, x.AccountId },
                    principalTable: "ProjectMembers",
                    principalColumns: new[] { "ProjectId", "AccountId" },
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ProjectMemberRoles_ProjectRoles_ProjectRoleId",
                    column: x => x.ProjectRoleId,
                    principalTable: "ProjectRoles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "TaskItemComments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TaskItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AuthorAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Content = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaskItemComments", x => x.Id);
                table.ForeignKey(
                    name: "FK_TaskItemComments_Accounts_AuthorAccountId",
                    column: x => x.AuthorAccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_TaskItemComments_TaskItems_TaskItemId",
                    column: x => x.TaskItemId,
                    principalTable: "TaskItems",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "TaskItemHistories",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TaskItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ActorAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Action = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                Snapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaskItemHistories", x => x.Id);
                table.ForeignKey(
                    name: "FK_TaskItemHistories_Accounts_ActorAccountId",
                    column: x => x.ActorAccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_TaskItemHistories_TaskItems_TaskItemId",
                    column: x => x.TaskItemId,
                    principalTable: "TaskItems",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.InsertData(
            table: "Functions",
            columns: new[] { "Id", "Code", "Name" },
            values: new object[,]
            {
                { new Guid("09ddef94-8813-feef-fe5d-113423733126"), "tasks.read", "tasks.read" },
                { new Guid("1178b17c-1c93-c9c8-7a84-520956293ab7"), "tasks.update-any", "tasks.update-any" },
                { new Guid("12463406-673b-7083-4a81-8989f67b6189"), "projects.manage-all", "projects.manage-all" },
                { new Guid("1fef387c-5830-f6fc-2b51-d9955724ea9d"), "accounts.manage-status", "accounts.manage-status" },
                { new Guid("225d2c8d-5643-edcc-6264-23023273f4aa"), "accounts.manage-role", "accounts.manage-role" },
                { new Guid("2d66f26d-2a0f-2f53-5736-21839ccc49aa"), "projects.read", "projects.read" },
                { new Guid("3043697f-c4a1-6c56-a8f1-b346752d6c2d"), "comments.read", "comments.read" },
                { new Guid("33a2bbef-fec3-46a8-1e77-b056011221ab"), "accounts.read", "accounts.read" },
                { new Guid("5974465b-fb6d-0942-9e6c-c47a5eeff057"), "comments.update-own", "comments.update-own" },
                { new Guid("61d4b140-5240-a863-0752-e6a6f15fc5d7"), "comments.create", "comments.create" },
                { new Guid("642b166d-0bae-4f78-8547-0936c056c947"), "tasks.delete", "tasks.delete" },
                { new Guid("7ad8cc35-c156-8e03-1f0f-5dce46d7b02d"), "project-members.manage-all", "project-members.manage-all" },
                { new Guid("9bec3fcd-3ad2-f414-e231-d82c767cd9c8"), "tasks.update-assigned", "tasks.update-assigned" },
                { new Guid("ae86667d-d332-f2ec-9478-dc11ccfe5538"), "tasks.create", "tasks.create" },
                { new Guid("c2607bb0-d043-de33-2bf9-57e2a4543ca6"), "comments.delete-own", "comments.delete-own" },
                { new Guid("f7a62720-6098-cfd6-fb68-68398da844f5"), "preferences.read-own", "preferences.read-own" },
                { new Guid("fb7562ac-dbd6-04a0-61b5-54989e957389"), "projects.create", "projects.create" },
                { new Guid("fe9db95a-0800-4e06-4076-737b9a06341d"), "preferences.update-own", "preferences.update-own" }
            });

        migrationBuilder.InsertData(
            table: "ProjectRoles",
            columns: new[] { "Id", "Code", "Name" },
            values: new object[,]
            {
                { new Guid("01110c23-5525-8940-afd8-ad83d7233315"), "SystemAnalyst", "SystemAnalyst" },
                { new Guid("574ad972-8842-17a4-fcb5-6cfc8036198e"), "FrontendDeveloper", "FrontendDeveloper" },
                { new Guid("72d1d8f5-5450-875d-977d-f44c03207e3e"), "BackendDeveloper", "BackendDeveloper" },
                { new Guid("8d87e5bf-86ea-3463-29e1-2d0960aabaae"), "Member", "Member" },
                { new Guid("dcb5403b-db13-d3f9-d0f9-ea2a538dbc52"), "ProjectManager", "ProjectManager" }
            });

        migrationBuilder.InsertData(
            table: "Roles",
            columns: new[] { "Id", "ConcurrencyStamp", "Description", "Name", "NormalizedName" },
            values: new object[,]
            {
                { new Guid("0a787aeb-cdd8-4f4d-bad5-443dde035c41"), "seed-user", "一般使用者", "User", "USER" },
                { new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc"), "seed-admin", "最高管理者", "Admin", "ADMIN" },
                { new Guid("992f6db2-93c5-ffd9-4d68-7bc13e3bf5f5"), "seed-viewer", "瀏覽者", "Viewer", "VIEWER" },
                { new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07"), "seed-administrator", "後台管理員", "Administrator", "ADMINISTRATOR" }
            });

        migrationBuilder.InsertData(
            table: "RoleFunctions",
            columns: new[] { "FunctionId", "RoleId" },
            values: new object[,]
            {
                { new Guid("09ddef94-8813-feef-fe5d-113423733126"), new Guid("0a787aeb-cdd8-4f4d-bad5-443dde035c41") },
                { new Guid("2d66f26d-2a0f-2f53-5736-21839ccc49aa"), new Guid("0a787aeb-cdd8-4f4d-bad5-443dde035c41") },
                { new Guid("3043697f-c4a1-6c56-a8f1-b346752d6c2d"), new Guid("0a787aeb-cdd8-4f4d-bad5-443dde035c41") },
                { new Guid("5974465b-fb6d-0942-9e6c-c47a5eeff057"), new Guid("0a787aeb-cdd8-4f4d-bad5-443dde035c41") },
                { new Guid("61d4b140-5240-a863-0752-e6a6f15fc5d7"), new Guid("0a787aeb-cdd8-4f4d-bad5-443dde035c41") },
                { new Guid("9bec3fcd-3ad2-f414-e231-d82c767cd9c8"), new Guid("0a787aeb-cdd8-4f4d-bad5-443dde035c41") },
                { new Guid("c2607bb0-d043-de33-2bf9-57e2a4543ca6"), new Guid("0a787aeb-cdd8-4f4d-bad5-443dde035c41") },
                { new Guid("f7a62720-6098-cfd6-fb68-68398da844f5"), new Guid("0a787aeb-cdd8-4f4d-bad5-443dde035c41") },
                { new Guid("fe9db95a-0800-4e06-4076-737b9a06341d"), new Guid("0a787aeb-cdd8-4f4d-bad5-443dde035c41") },
                { new Guid("09ddef94-8813-feef-fe5d-113423733126"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("1178b17c-1c93-c9c8-7a84-520956293ab7"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("12463406-673b-7083-4a81-8989f67b6189"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("1fef387c-5830-f6fc-2b51-d9955724ea9d"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("225d2c8d-5643-edcc-6264-23023273f4aa"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("2d66f26d-2a0f-2f53-5736-21839ccc49aa"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("3043697f-c4a1-6c56-a8f1-b346752d6c2d"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("33a2bbef-fec3-46a8-1e77-b056011221ab"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("5974465b-fb6d-0942-9e6c-c47a5eeff057"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("61d4b140-5240-a863-0752-e6a6f15fc5d7"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("642b166d-0bae-4f78-8547-0936c056c947"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("7ad8cc35-c156-8e03-1f0f-5dce46d7b02d"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("9bec3fcd-3ad2-f414-e231-d82c767cd9c8"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("ae86667d-d332-f2ec-9478-dc11ccfe5538"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("c2607bb0-d043-de33-2bf9-57e2a4543ca6"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("f7a62720-6098-cfd6-fb68-68398da844f5"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("fb7562ac-dbd6-04a0-61b5-54989e957389"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("fe9db95a-0800-4e06-4076-737b9a06341d"), new Guid("1adad986-a866-5e9d-7ab2-4273017f01fc") },
                { new Guid("09ddef94-8813-feef-fe5d-113423733126"), new Guid("992f6db2-93c5-ffd9-4d68-7bc13e3bf5f5") },
                { new Guid("2d66f26d-2a0f-2f53-5736-21839ccc49aa"), new Guid("992f6db2-93c5-ffd9-4d68-7bc13e3bf5f5") },
                { new Guid("3043697f-c4a1-6c56-a8f1-b346752d6c2d"), new Guid("992f6db2-93c5-ffd9-4d68-7bc13e3bf5f5") },
                { new Guid("f7a62720-6098-cfd6-fb68-68398da844f5"), new Guid("992f6db2-93c5-ffd9-4d68-7bc13e3bf5f5") },
                { new Guid("09ddef94-8813-feef-fe5d-113423733126"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("1178b17c-1c93-c9c8-7a84-520956293ab7"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("12463406-673b-7083-4a81-8989f67b6189"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("2d66f26d-2a0f-2f53-5736-21839ccc49aa"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("3043697f-c4a1-6c56-a8f1-b346752d6c2d"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("33a2bbef-fec3-46a8-1e77-b056011221ab"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("5974465b-fb6d-0942-9e6c-c47a5eeff057"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("61d4b140-5240-a863-0752-e6a6f15fc5d7"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("642b166d-0bae-4f78-8547-0936c056c947"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("7ad8cc35-c156-8e03-1f0f-5dce46d7b02d"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("ae86667d-d332-f2ec-9478-dc11ccfe5538"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("c2607bb0-d043-de33-2bf9-57e2a4543ca6"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("f7a62720-6098-cfd6-fb68-68398da844f5"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("fb7562ac-dbd6-04a0-61b5-54989e957389"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") },
                { new Guid("fe9db95a-0800-4e06-4076-737b9a06341d"), new Guid("df98f6d0-8bd9-a99a-f62d-679a362d5b07") }
            });

        migrationBuilder.CreateIndex(
            name: "IX_AccountClaims_UserId",
            table: "AccountClaims",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AccountLogins_UserId",
            table: "AccountLogins",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AccountRoles_RoleId",
            table: "AccountRoles",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "IX_AccountRoles_UserId",
            table: "AccountRoles",
            column: "UserId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "EmailIndex",
            table: "Accounts",
            column: "NormalizedEmail");

        migrationBuilder.CreateIndex(
            name: "UserNameIndex",
            table: "Accounts",
            column: "NormalizedUserName",
            unique: true,
            filter: "[NormalizedUserName] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_AuditLogs_ActorAccountId",
            table: "AuditLogs",
            column: "ActorAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_AuditLogs_EntityType_EntityId_CreatedAt",
            table: "AuditLogs",
            columns: new[] { "EntityType", "EntityId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_EmailMessages_Status_CreatedAt",
            table: "EmailMessages",
            columns: new[] { "Status", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Functions_Code",
            table: "Functions",
            column: "Code",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ProjectMemberRoles_ProjectRoleId",
            table: "ProjectMemberRoles",
            column: "ProjectRoleId");

        migrationBuilder.CreateIndex(
            name: "IX_ProjectMembers_AccountId",
            table: "ProjectMembers",
            column: "AccountId");

        migrationBuilder.CreateIndex(
            name: "IX_ProjectRoles_Code",
            table: "ProjectRoles",
            column: "Code",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Projects_Code",
            table: "Projects",
            column: "Code",
            unique: true,
            filter: "[DeletedAt] IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_Projects_OwnerAccountId_Status",
            table: "Projects",
            columns: new[] { "OwnerAccountId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokens_AccountId_FamilyId",
            table: "RefreshTokens",
            columns: new[] { "AccountId", "FamilyId" });

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokens_TokenHash",
            table: "RefreshTokens",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RoleClaims_RoleId",
            table: "RoleClaims",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "IX_RoleFunctions_FunctionId",
            table: "RoleFunctions",
            column: "FunctionId");

        migrationBuilder.CreateIndex(
            name: "RoleNameIndex",
            table: "Roles",
            column: "NormalizedName",
            unique: true,
            filter: "[NormalizedName] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_TaskItemComments_AuthorAccountId",
            table: "TaskItemComments",
            column: "AuthorAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_TaskItemComments_TaskItemId_CreatedAt",
            table: "TaskItemComments",
            columns: new[] { "TaskItemId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_TaskItemHistories_ActorAccountId",
            table: "TaskItemHistories",
            column: "ActorAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_TaskItemHistories_TaskItemId_CreatedAt",
            table: "TaskItemHistories",
            columns: new[] { "TaskItemId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_TaskItems_AssignedAccountId",
            table: "TaskItems",
            column: "AssignedAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_TaskItems_Code",
            table: "TaskItems",
            column: "Code",
            unique: true,
            filter: "[DeletedAt] IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_TaskItems_CreatedByAccountId",
            table: "TaskItems",
            column: "CreatedByAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_TaskItems_ProjectId_Status_Deadline",
            table: "TaskItems",
            columns: new[] { "ProjectId", "Status", "Deadline" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AccountClaims");

        migrationBuilder.DropTable(
            name: "AccountLogins");

        migrationBuilder.DropTable(
            name: "AccountRoles");

        migrationBuilder.DropTable(
            name: "AccountTokens");

        migrationBuilder.DropTable(
            name: "AuditLogs");

        migrationBuilder.DropTable(
            name: "EmailMessages");

        migrationBuilder.DropTable(
            name: "ProjectMemberRoles");

        migrationBuilder.DropTable(
            name: "RefreshTokens");

        migrationBuilder.DropTable(
            name: "RoleClaims");

        migrationBuilder.DropTable(
            name: "RoleFunctions");

        migrationBuilder.DropTable(
            name: "TaskItemComments");

        migrationBuilder.DropTable(
            name: "TaskItemHistories");

        migrationBuilder.DropTable(
            name: "UserPreferences");

        migrationBuilder.DropTable(
            name: "ProjectMembers");

        migrationBuilder.DropTable(
            name: "ProjectRoles");

        migrationBuilder.DropTable(
            name: "Functions");

        migrationBuilder.DropTable(
            name: "Roles");

        migrationBuilder.DropTable(
            name: "TaskItems");

        migrationBuilder.DropTable(
            name: "Projects");

        migrationBuilder.DropTable(
            name: "Accounts");
    }
}
