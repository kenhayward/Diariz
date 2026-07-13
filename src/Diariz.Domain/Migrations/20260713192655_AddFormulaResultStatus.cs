using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddFormulaResultStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "FormulaResults",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "FormulaResults",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing rows are all completed runs, so mark them Ready (1). New rows default to Generating (0).
            migrationBuilder.Sql("UPDATE \"FormulaResults\" SET \"Status\" = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Error",
                table: "FormulaResults");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "FormulaResults");
        }
    }
}
