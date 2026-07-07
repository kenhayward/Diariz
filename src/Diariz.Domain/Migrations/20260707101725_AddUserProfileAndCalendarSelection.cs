using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddUserProfileAndCalendarSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompanyDescription",
                table: "UserSettings",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompanyName",
                table: "UserSettings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleSelectedCalendarIdsJson",
                table: "UserSettings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobDescription",
                table: "UserSettings",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobTitle",
                table: "UserSettings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkedIn",
                table: "UserSettings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Theme",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompanyDescription",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "CompanyName",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "GoogleSelectedCalendarIdsJson",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "JobDescription",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "JobTitle",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "LinkedIn",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "Theme",
                table: "UserSettings");
        }
    }
}
