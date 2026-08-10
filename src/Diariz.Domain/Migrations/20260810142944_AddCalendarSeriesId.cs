using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarSeriesId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SeriesId",
                table: "RecordingCalendarLinks",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            // Backfill, so an established weekly meeting shows its history on day one rather than starting
            // empty. Done here and not in the Seeder: the Seeder runs on every boot, so a data move there
            // re-applies itself and undoes whatever the user did in between.

            // Google expands a series into instances as {masterId}_{yyyyMMddTHHmmssZ}, and our own .ics
            // MakeId produces the same shape. An .ics UID may itself contain '_', so match the timestamp
            // suffix rather than splitting on the first separator.
            migrationBuilder.Sql("""
                UPDATE "RecordingCalendarLinks"
                SET "SeriesId" = regexp_replace("EventId", '_[0-9]{8}T[0-9]{6}Z$', '')
                WHERE "SeriesId" IS NULL
                  AND "EventId" ~ '_[0-9]{8}T[0-9]{6}Z$'
                  AND "EventId" NOT LIKE 'outlook:%';
                """);

            // Outlook's public event id is 'outlook:' + the mirror row's Guid (OutlookEventId.EventKey), and a
            // recurring occurrence's uid is {series}#{start}. Only occurrences still inside the rolling window
            // can be resolved; older links stay null, which reads as "no series I can identify".
            migrationBuilder.Sql("""
                UPDATE "RecordingCalendarLinks" l
                SET "SeriesId" = split_part(e."Uid", '#', 1)
                FROM "OutlookCalendarEvents" e
                WHERE l."SeriesId" IS NULL
                  AND e."IsRecurring"
                  AND l."EventId" = 'outlook:' || e."Id"::text;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "RecordingCalendarLinks");
        }
    }
}
