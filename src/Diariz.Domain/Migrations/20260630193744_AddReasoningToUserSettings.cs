using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddReasoningToUserSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReasoningEffort",
                table: "UserSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReasoningEnabled",
                table: "UserSettings",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReasoningEffort",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ReasoningEnabled",
                table: "UserSettings");
        }
    }
}
