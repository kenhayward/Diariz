using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <summary>A formula stops being a free-text prompt and becomes a structured template (the same shape a
    /// meeting type's minutes template uses).
    ///
    /// Order matters: add the column, <b>backfill it from Prompt</b>, and only then drop Prompt. The scaffolded
    /// version dropped Prompt first, which would have destroyed every formula on the instance.
    ///
    /// The backfill wraps each prompt in one headless (level-0) section holding one prompt block - the shape
    /// <c>TemplateContent.FromPrompt</c> produces. That composes to exactly the prompt's own output, so every
    /// existing formula keeps behaving identically.</summary>
    public partial class AddFormulaContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentJson",
                table: "Formulas",
                type: "jsonb",
                nullable: false,
                defaultValue: "{\"sections\":[]}");

            // Backfill before dropping the source column. jsonb_build_* keeps the prompt correctly escaped
            // whatever it contains (quotes, newlines, backslashes) - never string-concatenate JSON here.
            migrationBuilder.Sql("""
                UPDATE "Formulas"
                SET "ContentJson" = jsonb_build_object(
                    'sections', jsonb_build_array(
                        jsonb_build_object(
                            'level', 0,
                            'title', '',
                            'blocks', jsonb_build_array(
                                jsonb_build_object('kind', 'prompt', 'text', "Prompt")
                            )
                        )
                    )
                );
                """);

            migrationBuilder.DropColumn(
                name: "Prompt",
                table: "Formulas");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Prompt",
                table: "Formulas",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Recover the prompt from a headless single-prompt template. A formula with real structure (headings,
            // fields, several prompt blocks) cannot be expressed as a single prompt, so it degrades to its first
            // prompt block - the closest thing the old model can hold. Down is a break-glass path, not a lossless
            // inverse.
            migrationBuilder.Sql("""
                UPDATE "Formulas"
                SET "Prompt" = COALESCE(
                    (
                        SELECT b ->> 'text'
                        FROM jsonb_array_elements("ContentJson" -> 'sections') AS s,
                             jsonb_array_elements(s -> 'blocks') AS b
                        WHERE b ->> 'kind' = 'prompt'
                        LIMIT 1
                    ), '');
                """);

            migrationBuilder.DropColumn(
                name: "ContentJson",
                table: "Formulas");
        }
    }
}
