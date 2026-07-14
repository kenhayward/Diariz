using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <summary>A meeting type stops owning a template and starts <b>pointing at a formula</b>. It keeps only its
    /// presentation (title, group, icon, colour, overview); the document it produces is its primary formula's.
    ///
    /// <para><b>Order matters.</b> Add the column and the join table, <b>convert every meeting type's ContentJson
    /// into a Formula</b>, link it, and only then drop ContentJson. The scaffolded version dropped it first, which
    /// would have destroyed every minutes template on the instance - including a Platform Administrator's edits to
    /// the seeded standards, which the seeder is otherwise careful never to overwrite.</para>
    ///
    /// <para>The conversion <b>preserves scope</b>, so permissions are unchanged: a seeded standard becomes a
    /// built-in Diariz formula (so it can't be deleted out from under its template), an admin-created Platform type
    /// becomes a Platform formula, and a user's Personal type becomes a Personal formula they own.</para>
    ///
    /// <para>Because the data is carried forward rather than discarded, an older backup still restores and is rolled
    /// up by this migration - so <c>MaintenanceController.CurrentFormat</c> does <b>not</b> need bumping.</para></summary>
    public partial class MeetingTypesPointAtFormulas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PrimaryFormulaId",
                table: "MeetingTypes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MeetingTypeFormulas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    FormulaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingTypeFormulas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeetingTypeFormulas_Formulas_FormulaId",
                        column: x => x.FormulaId,
                        principalTable: "Formulas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MeetingTypeFormulas_MeetingTypes_MeetingTypeId",
                        column: x => x.MeetingTypeId,
                        principalTable: "MeetingTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingTypes_PrimaryFormulaId",
                table: "MeetingTypes",
                column: "PrimaryFormulaId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingTypeFormulas_FormulaId",
                table: "MeetingTypeFormulas",
                column: "FormulaId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingTypeFormulas_MeetingTypeId_FormulaId",
                table: "MeetingTypeFormulas",
                columns: new[] { "MeetingTypeId", "FormulaId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MeetingTypes_Formulas_PrimaryFormulaId",
                table: "MeetingTypes",
                column: "PrimaryFormulaId",
                principalTable: "Formulas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ---- Convert: one Formula per meeting type, carrying its template. THEN drop ContentJson. ----
            //
            // Scope is derived so permissions survive untouched:
            //   Key IS NOT NULL  -> a seeded standard      -> Diariz (2), IsBuiltIn (undeletable)
            //   UserId IS NULL   -> an admin Platform type -> Platform (1)
            //   otherwise        -> a user's Personal type -> Personal (0), owned by that user
            //
            // Context = Transcript(1) | Notes(2) | Actions(32) = 35: what a minutes template needs to see - the
            // transcript, the note-taker's lines (they steer every section), and the canonical actions (the
            // `action_items` field renders them).
            migrationBuilder.Sql("""
                CREATE TEMPORARY TABLE mt_formula_map AS
                SELECT m."Id" AS mt_id, gen_random_uuid() AS f_id
                FROM "MeetingTypes" m;

                INSERT INTO "Formulas" (
                    "Id", "Scope", "OwnerUserId", "Name", "Description", "ContentJson", "Context",
                    "Enabled", "Shared", "IsBuiltIn", "CreatedAt", "UpdatedAt")
                SELECT
                    map.f_id,
                    CASE
                        WHEN m."Key" IS NOT NULL THEN 2
                        WHEN m."UserId" IS NULL  THEN 1
                        ELSE 0
                    END,
                    m."UserId",
                    left(m."Title" || ' minutes', 256),
                    left(NULLIF(m."Overview", ''), 1024),
                    m."ContentJson",
                    35,
                    true,
                    false,
                    (m."Key" IS NOT NULL),
                    now(),
                    now()
                FROM "MeetingTypes" m
                JOIN mt_formula_map map ON map.mt_id = m."Id";

                UPDATE "MeetingTypes" m
                SET "PrimaryFormulaId" = map.f_id
                FROM mt_formula_map map
                WHERE map.mt_id = m."Id";

                DROP TABLE mt_formula_map;
                """);

            migrationBuilder.DropColumn(
                name: "ContentJson",
                table: "MeetingTypes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentJson",
                table: "MeetingTypes",
                type: "jsonb",
                nullable: false,
                defaultValue: "{\"sections\":[]}");

            // Copy the primary formula's template back onto the type before the link is dropped. Break-glass, not
            // a lossless inverse: the formulas created by Up are left behind (deleting them could take real user
            // formulas with them), and additional-formula links are simply lost - the old model can't express them.
            migrationBuilder.Sql("""
                UPDATE "MeetingTypes" m
                SET "ContentJson" = f."ContentJson"
                FROM "Formulas" f
                WHERE f."Id" = m."PrimaryFormulaId";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_MeetingTypes_Formulas_PrimaryFormulaId",
                table: "MeetingTypes");

            migrationBuilder.DropTable(
                name: "MeetingTypeFormulas");

            migrationBuilder.DropIndex(
                name: "IX_MeetingTypes_PrimaryFormulaId",
                table: "MeetingTypes");

            migrationBuilder.DropColumn(
                name: "PrimaryFormulaId",
                table: "MeetingTypes");
        }
    }
}
