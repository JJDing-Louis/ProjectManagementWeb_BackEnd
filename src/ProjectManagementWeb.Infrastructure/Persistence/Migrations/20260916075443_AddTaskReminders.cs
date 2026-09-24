using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectManagementWeb.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddTaskReminders : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProjectReminderRuns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ReminderDate = table.Column<DateOnly>(type: "date", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                CreatedCount = table.Column<int>(type: "int", nullable: false),
                DuplicateCount = table.Column<int>(type: "int", nullable: false),
                SkippedCount = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProjectReminderRuns", x => x.Id);
                table.ForeignKey(
                    name: "FK_ProjectReminderRuns_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateTable(
            name: "TaskReminders",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TaskItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RecipientAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ReminderDate = table.Column<DateOnly>(type: "date", nullable: false),
                Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                AttemptCount = table.Column<int>(type: "int", nullable: false),
                RetryCount = table.Column<int>(type: "int", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ClaimedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                ClaimToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                ProviderResponseId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                CancellationReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                AlertedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TaskReminders", x => x.Id);
                table.ForeignKey(
                    name: "FK_TaskReminders_Accounts_RecipientAccountId",
                    column: x => x.RecipientAccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id");
                table.ForeignKey(
                    name: "FK_TaskReminders_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id");
                table.ForeignKey(
                    name: "FK_TaskReminders_TaskItems_TaskItemId",
                    column: x => x.TaskItemId,
                    principalTable: "TaskItems",
                    principalColumn: "Id");
            });

        migrationBuilder.CreateIndex(
            name: "IX_ProjectReminderRuns_ProjectId_ReminderDate",
            table: "ProjectReminderRuns",
            columns: new[] { "ProjectId", "ReminderDate" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TaskReminders_ProjectId",
            table: "TaskReminders",
            column: "ProjectId");

        migrationBuilder.CreateIndex(
            name: "IX_TaskReminders_RecipientAccountId",
            table: "TaskReminders",
            column: "RecipientAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_TaskReminders_Status_NextAttemptAt",
            table: "TaskReminders",
            columns: new[] { "Status", "NextAttemptAt" });

        migrationBuilder.CreateIndex(
            name: "IX_TaskReminders_TaskItemId_RecipientAccountId_ReminderDate",
            table: "TaskReminders",
            columns: new[] { "TaskItemId", "RecipientAccountId", "ReminderDate" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ProjectReminderRuns");

        migrationBuilder.DropTable(
            name: "TaskReminders");
    }
}
