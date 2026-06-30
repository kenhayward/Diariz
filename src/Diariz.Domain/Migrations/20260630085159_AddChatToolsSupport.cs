using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddChatToolsSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChatToolOverridesJson",
                table: "UserSettings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ChatToolsEnabled",
                table: "UserSettings",
                type: "boolean",
                nullable: true);

            // Fuzzy transcript search (the built-in chat tools) is backed by a trigram index. The index is
            // on coalesce("Revised","Original") so it matches the effective (possibly user-edited) text.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_Segments_Text_Trgm\" ON \"Segments\" " +
                "USING gin ((coalesce(\"Revised\", \"Original\")) gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Segments_Text_Trgm\";");

            migrationBuilder.DropColumn(
                name: "ChatToolOverridesJson",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ChatToolsEnabled",
                table: "UserSettings");
        }
    }
}
