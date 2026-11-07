using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordingProcessingOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoExtractActions",
                table: "Recordings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoExtractTags",
                table: "Recordings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoGenerateEmbeddings",
                table: "Recordings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoGenerateMinutes",
                table: "Recordings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoGenerateSummary",
                table: "Recordings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoExtractActions",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "AutoExtractTags",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "AutoGenerateEmbeddings",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "AutoGenerateMinutes",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "AutoGenerateSummary",
                table: "Recordings");
        }
    }
}
