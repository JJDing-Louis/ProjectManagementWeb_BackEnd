using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectManagementWeb.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddBusinessCodeCounters : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BusinessCodeCounters",
            columns: table => new
            {
                CodeType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                LastValue = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BusinessCodeCounters", x => new { x.CodeType, x.BusinessDate });
                table.CheckConstraint("CK_BusinessCodeCounters_CodeType", "[CodeType] IN ('Project', 'Task')");
                table.CheckConstraint("CK_BusinessCodeCounters_LastValue", "[LastValue] BETWEEN 1 AND 999999");
            });

        migrationBuilder.Sql("""
            INSERT INTO [BusinessCodeCounters] ([CodeType], [BusinessDate], [LastValue])
            SELECT 'Project', Parsed.BusinessDate, MAX(Parsed.SequenceValue)
            FROM [Projects]
            CROSS APPLY (SELECT
                TRY_CONVERT(date, SUBSTRING([Code], 5, 8), 112) AS BusinessDate,
                TRY_CONVERT(int, SUBSTRING([Code], 13, 6)) AS SequenceValue) Parsed
            WHERE LEN([Code]) = 18 AND [Code] LIKE 'PRJ-%'
              AND Parsed.BusinessDate IS NOT NULL
              AND Parsed.SequenceValue BETWEEN 1 AND 999999
            GROUP BY Parsed.BusinessDate;

            INSERT INTO [BusinessCodeCounters] ([CodeType], [BusinessDate], [LastValue])
            SELECT 'Task', Parsed.BusinessDate, MAX(Parsed.SequenceValue)
            FROM [TaskItems]
            CROSS APPLY (SELECT
                TRY_CONVERT(date, SUBSTRING([Code], 6, 8), 112) AS BusinessDate,
                TRY_CONVERT(int, SUBSTRING([Code], 14, 6)) AS SequenceValue) Parsed
            WHERE LEN([Code]) = 19 AND [Code] LIKE 'TASK-%'
              AND Parsed.BusinessDate IS NOT NULL
              AND Parsed.SequenceValue BETWEEN 1 AND 999999
            GROUP BY Parsed.BusinessDate;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "BusinessCodeCounters");
    }
}
