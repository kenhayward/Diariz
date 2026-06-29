using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentOriginalRevised : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Text",
                table: "Segments",
                newName: "Original");

            migrationBuilder.AddColumn<string>(
                name: "Revised",
                table: "Segments",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revised",
                table: "Segments");

            migrationBuilder.RenameColumn(
                name: "Original",
                table: "Segments",
                newName: "Text");
        }
    }
}
