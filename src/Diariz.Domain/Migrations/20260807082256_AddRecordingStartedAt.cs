using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordingStartedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndedAt",
                table: "Recordings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAt",
                table: "Recordings",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill an estimated start for existing recordings so the calendar match improves retroactively
            // rather than only for new takes. CreatedAt is when the upload landed, so CreatedAt - DurationMs is
            // roughly when capture began; it ignores upload latency, which is small next to the recording-length
            // error it replaces. Deliberately skipped:
            //   Source = 2 (Upload) - an uploaded file's CreatedAt says when it was uploaded, which tells us
            //     nothing about when the audio was recorded, so subtracting the duration would invent a time.
            //   DurationMs = 0 - nothing to subtract, and it would just restate CreatedAt.
            // EndedAt is left null on backfilled rows, so recEnd falls back to StartedAt + DurationMs = CreatedAt,
            // i.e. exactly the pre-migration behaviour rather than a guess.
            // `bigint * interval` is exact and needs no cast, unlike make_interval(secs => ...) which would
            // rely on an implicit numeric -> double precision conversion.
            migrationBuilder.Sql("""
                UPDATE "Recordings"
                   SET "StartedAt" = "CreatedAt" - ("DurationMs" * INTERVAL '1 millisecond')
                 WHERE "Source" <> 2 AND "DurationMs" > 0;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_UserId_StartedAt",
                table: "Recordings",
                columns: new[] { "UserId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Recordings_UserId_StartedAt",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "EndedAt",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "Recordings");
        }
    }
}
