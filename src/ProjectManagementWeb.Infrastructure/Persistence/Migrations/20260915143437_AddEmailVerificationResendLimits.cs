using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectManagementWeb.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddEmailVerificationResendLimits : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "EmailVerificationResendAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ClientAddressHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EmailVerificationResendAttempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_EmailVerificationResendAttempts_Accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_EmailVerificationResendAttempts_AccountId_Outcome_RequestedAt",
            table: "EmailVerificationResendAttempts",
            columns: new[] { "AccountId", "Outcome", "RequestedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_EmailVerificationResendAttempts_ClientAddressHash_RequestedAt",
            table: "EmailVerificationResendAttempts",
            columns: new[] { "ClientAddressHash", "RequestedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "EmailVerificationResendAttempts");
    }
}
