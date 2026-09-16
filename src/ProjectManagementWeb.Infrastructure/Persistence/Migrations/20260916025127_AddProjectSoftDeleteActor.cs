using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectManagementWeb.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddProjectSoftDeleteActor : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "DeletedByAccountId",
            table: "Projects",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Projects_DeletedByAccountId",
            table: "Projects",
            column: "DeletedByAccountId");

        migrationBuilder.AddForeignKey(
            name: "FK_Projects_Accounts_DeletedByAccountId",
            table: "Projects",
            column: "DeletedByAccountId",
            principalTable: "Accounts",
            principalColumn: "Id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Projects_Accounts_DeletedByAccountId",
            table: "Projects");

        migrationBuilder.DropIndex(
            name: "IX_Projects_DeletedByAccountId",
            table: "Projects");

        migrationBuilder.DropColumn(
            name: "DeletedByAccountId",
            table: "Projects");
    }
}
