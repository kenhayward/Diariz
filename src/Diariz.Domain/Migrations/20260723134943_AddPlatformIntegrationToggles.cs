using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformIntegrationToggles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "McpAccessEnabled",
                table: "PlatformSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "WebhooksEnabled",
                table: "PlatformSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "PlatformSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "McpAccessEnabled", "WebhooksEnabled" },
                values: new object[] { true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "McpAccessEnabled",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "WebhooksEnabled",
                table: "PlatformSettings");
        }
    }
}
