using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectManagementWeb.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddEmailVerificationTokenLifecycle : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "EmailVerificationTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EmailMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ActivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                InvalidatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EmailVerificationTokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_EmailVerificationTokens_Accounts_AccountId",
                    column: x => x.AccountId,
                    principalTable: "Accounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_EmailVerificationTokens_EmailMessages_EmailMessageId",
                    column: x => x.EmailMessageId,
                    principalTable: "EmailMessages",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_EmailVerificationTokens_AccountId",
            table: "EmailVerificationTokens",
            column: "AccountId",
            unique: true,
            filter: "[ActivatedAt] IS NOT NULL AND [UsedAt] IS NULL AND [InvalidatedAt] IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_EmailVerificationTokens_EmailMessageId",
            table: "EmailVerificationTokens",
            column: "EmailMessageId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_EmailVerificationTokens_ExpiresAt",
            table: "EmailVerificationTokens",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_EmailVerificationTokens_TokenHash",
            table: "EmailVerificationTokens",
            column: "TokenHash",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "EmailVerificationTokens");
    }
}
