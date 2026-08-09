using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarRecordingPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CalendarAutoStopAfterMinutes",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<bool>(
                name: "CalendarAutoStopEnabled",
                table: "UserSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CalendarSilenceStopSeconds",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalendarAutoStopAfterMinutes",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "CalendarAutoStopEnabled",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "CalendarSilenceStopSeconds",
                table: "UserSettings");
        }
    }
}
