using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class PlatformEmbeddingEndpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmbeddingApiBase",
                table: "PlatformSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingApiKeyEncrypted",
                table: "PlatformSettings",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "PlatformSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "EmbeddingApiBase", "EmbeddingApiKeyEncrypted" },
                values: new object[] { null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmbeddingApiBase",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "EmbeddingApiKeyEncrypted",
                table: "PlatformSettings");
        }
    }
}
