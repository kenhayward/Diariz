using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <summary>Drops the seven per-user LLM columns, whose configuration moved to the platform in 0.221.0.
    ///
    /// <b>Deliberately NOT paired with a MaintenanceController.CurrentFormat bump</b>, even though the house
    /// rule flags destructive drops. Restore does <c>pg_restore --clean</c> and then migrates forward, so an
    /// older backup restores its own columns and this migration then drops them: the restore succeeds and
    /// the platform is left correct. The only loss is the per-user values themselves, which this release
    /// discards by design and which nothing reads any more. Bumping the format would hard-reject every
    /// backup taken before 0.221.0 for no safety gain.</summary>
    public partial class DropPerUserLlmSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChatContextWindow",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "LlmTimeoutSeconds",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ReasoningEffort",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ReasoningEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "SummaryApiBase",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "SummaryApiKeyEncrypted",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "SummaryModel",
                table: "UserSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChatContextWindow",
                table: "UserSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LlmTimeoutSeconds",
                table: "UserSettings",
                type: "integer",
                nullable: true);

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

            migrationBuilder.AddColumn<string>(
                name: "SummaryApiBase",
                table: "UserSettings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SummaryApiKeyEncrypted",
                table: "UserSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SummaryModel",
                table: "UserSettings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }
    }
}
