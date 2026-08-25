using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentificationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "IdentificationConfirmBand",
                table: "PlatformSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.40000000000000002);

            migrationBuilder.AddColumn<double>(
                name: "IdentificationMargin",
                table: "PlatformSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.050000000000000003);

            migrationBuilder.AddColumn<int>(
                name: "IdentificationMinSpeechMs",
                table: "PlatformSettings",
                type: "integer",
                nullable: false,
                defaultValue: 3000);

            migrationBuilder.AddColumn<double>(
                name: "IdentificationThreshold",
                table: "PlatformSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.29999999999999999);

            migrationBuilder.UpdateData(
                table: "PlatformSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "IdentificationConfirmBand", "IdentificationMargin", "IdentificationMinSpeechMs", "IdentificationThreshold" },
                values: new object[] { 0.40000000000000002, 0.050000000000000003, 3000, 0.29999999999999999 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdentificationConfirmBand",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IdentificationMargin",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IdentificationMinSpeechMs",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IdentificationThreshold",
                table: "PlatformSettings");
        }
    }
}
