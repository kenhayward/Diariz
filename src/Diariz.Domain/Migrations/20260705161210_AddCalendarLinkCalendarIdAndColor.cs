using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarLinkCalendarIdAndColor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing links were all on the primary calendar, so backfill them to "primary".
            migrationBuilder.AddColumn<string>(
                name: "CalendarId",
                table: "RecordingCalendarLinks",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "primary");

            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "RecordingCalendarLinks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalendarId",
                table: "RecordingCalendarLinks");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "RecordingCalendarLinks");
        }
    }
}
