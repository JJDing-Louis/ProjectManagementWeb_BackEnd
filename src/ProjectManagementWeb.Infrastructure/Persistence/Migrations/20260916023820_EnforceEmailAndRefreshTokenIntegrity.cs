using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectManagementWeb.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class EnforceEmailAndRefreshTokenIntegrity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            IF EXISTS (SELECT 1 FROM [Accounts] WHERE [NormalizedEmail] IS NULL)
                THROW 51000, 'NormalizedEmail migration blocked: NULL data must be corrected manually.', 1;

            IF EXISTS (
                SELECT [NormalizedEmail]
                FROM [Accounts]
                GROUP BY [NormalizedEmail]
                HAVING COUNT(*) > 1)
                THROW 51001, 'NormalizedEmail migration blocked: duplicate data must be corrected manually.', 1;
            """);

        migrationBuilder.DropIndex(
            name: "EmailIndex",
            table: "Accounts");

        migrationBuilder.AlterColumn<string>(
            name: "NormalizedEmail",
            table: "Accounts",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(256)",
            oldMaxLength: 256,
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokens_ReplacedByTokenId",
            table: "RefreshTokens",
            column: "ReplacedByTokenId");

        migrationBuilder.CreateIndex(
            name: "EmailIndex",
            table: "Accounts",
            column: "NormalizedEmail",
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_RefreshTokens_RefreshTokens_ReplacedByTokenId",
            table: "RefreshTokens",
            column: "ReplacedByTokenId",
            principalTable: "RefreshTokens",
            principalColumn: "Id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_RefreshTokens_RefreshTokens_ReplacedByTokenId",
            table: "RefreshTokens");

        migrationBuilder.DropIndex(
            name: "IX_RefreshTokens_ReplacedByTokenId",
            table: "RefreshTokens");

        migrationBuilder.DropIndex(
            name: "EmailIndex",
            table: "Accounts");

        migrationBuilder.AlterColumn<string>(
            name: "NormalizedEmail",
            table: "Accounts",
            type: "nvarchar(256)",
            maxLength: 256,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "nvarchar(256)",
            oldMaxLength: 256);

        migrationBuilder.CreateIndex(
            name: "EmailIndex",
            table: "Accounts",
            column: "NormalizedEmail");
    }
}
