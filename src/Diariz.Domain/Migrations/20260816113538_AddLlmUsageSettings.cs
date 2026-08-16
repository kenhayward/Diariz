using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmUsageSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LlmStreamUsageEnabled",
                table: "PlatformSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "LlmUsageLoggingEnabled",
                table: "PlatformSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "LlmUsageRetentionDays",
                table: "PlatformSettings",
                type: "integer",
                nullable: false,
                defaultValue: 90);

            migrationBuilder.UpdateData(
                table: "PlatformSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "LlmStreamUsageEnabled", "LlmUsageLoggingEnabled", "LlmUsageRetentionDays" },
                values: new object[] { true, true, 90 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LlmStreamUsageEnabled",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "LlmUsageLoggingEnabled",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "LlmUsageRetentionDays",
                table: "PlatformSettings");
        }
    }
}
