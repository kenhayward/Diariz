using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddVoiceSampleSpans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SpansJson",
                table: "ProfileContributions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UsedMs",
                table: "ProfileContributions",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpansJson",
                table: "ProfileContributions");

            migrationBuilder.DropColumn(
                name: "UsedMs",
                table: "ProfileContributions");
        }
    }
}
