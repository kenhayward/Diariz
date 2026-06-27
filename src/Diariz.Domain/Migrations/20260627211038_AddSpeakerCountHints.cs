using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddSpeakerCountHints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxSpeakers",
                table: "Recordings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinSpeakers",
                table: "Recordings",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxSpeakers",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "MinSpeakers",
                table: "Recordings");
        }
    }
}
