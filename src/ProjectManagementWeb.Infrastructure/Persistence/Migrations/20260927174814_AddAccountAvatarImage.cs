using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectManagementWeb.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddAccountAvatarImage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "AvatarImage",
            table: "Accounts",
            type: "varbinary(max)",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AvatarImage",
            table: "Accounts");
    }
}
