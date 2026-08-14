using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordingTagStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Status 0 = Suggested. The default IS the demotion: every tag that exists today was applied by the
            // LLM without the user choosing it, so it becomes a suggestion. Same for an older backup restored
            // later, which is why this migration needs no data script and no CurrentFormat bump.
            migrationBuilder.AddColumn<int>(
                name: "Status", table: "RecordingTags", type: "integer", nullable: false, defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AdoptedAt", table: "RecordingTags", type: "timestamp with time zone", nullable: true);

            // The unique index below would fail on any legacy case-variant pair. The parse step already dedupes
            // case-insensitively and replace is wholesale, so this should delete nothing - but "should" is not a
            // deployment strategy. Keep the lowest Ordinal of each group.
            migrationBuilder.Sql("""
                DELETE FROM "RecordingTags" t
                USING "RecordingTags" other
                WHERE t."RecordingId" = other."RecordingId"
                  AND lower(t."Tag") = lower(other."Tag")
                  AND (t."Ordinal" > other."Ordinal"
                       OR (t."Ordinal" = other."Ordinal" AND t."Id" > other."Id"));
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_RecordingTags_RecordingId_TagLower"
                ON "RecordingTags" ("RecordingId", lower("Tag"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_RecordingTags_RecordingId_TagLower";""");
            migrationBuilder.DropColumn(name: "AdoptedAt", table: "RecordingTags");
            migrationBuilder.DropColumn(name: "Status", table: "RecordingTags");
        }
    }
}
