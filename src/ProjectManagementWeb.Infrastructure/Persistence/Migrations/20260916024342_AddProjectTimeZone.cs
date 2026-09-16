using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectManagementWeb.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddProjectTimeZone : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        string defaultTimeZoneId = ReadMigrationDefaultTimeZoneId();
        migrationBuilder.AddColumn<string>(
            name: "TimeZoneId",
            table: "Projects",
            type: "nvarchar(100)",
            maxLength: 100,
            nullable: true);
        migrationBuilder.Sql(
            $"UPDATE [Projects] SET [TimeZoneId] = N'{defaultTimeZoneId.Replace("'", "''", StringComparison.Ordinal)}' WHERE [TimeZoneId] IS NULL");
        migrationBuilder.AlterColumn<string>(
            name: "TimeZoneId",
            table: "Projects",
            type: "nvarchar(100)",
            maxLength: 100,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "nvarchar(100)",
            oldMaxLength: 100,
            oldNullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "TimeZoneId",
            table: "Projects");
    }

    private static string ReadMigrationDefaultTimeZoneId()
    {
        string value = Environment.GetEnvironmentVariable("PMW_MIGRATION_DEFAULT_TIME_ZONE_ID")?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 ||
            !TimeZoneInfo.TryConvertIanaIdToWindowsId(value, out _))
        {
            throw new InvalidOperationException(
                "PMW_MIGRATION_DEFAULT_TIME_ZONE_ID 必須設定為長度不超過 100 的合法 IANA timezone ID。");
        }
        return value;
    }
}
