using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectManagementWeb.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddLoginFailureRateLimits : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LoginFailureAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AccountKeyHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ClientAddressHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LoginFailureAttempts", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LoginFailureAttempts_AccountKeyHash_Outcome_OccurredAt",
            table: "LoginFailureAttempts",
            columns: new[] { "AccountKeyHash", "Outcome", "OccurredAt" });

        migrationBuilder.CreateIndex(
            name: "IX_LoginFailureAttempts_ClientAddressHash_Outcome_OccurredAt",
            table: "LoginFailureAttempts",
            columns: new[] { "ClientAddressHash", "Outcome", "OccurredAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "LoginFailureAttempts");
    }
}
