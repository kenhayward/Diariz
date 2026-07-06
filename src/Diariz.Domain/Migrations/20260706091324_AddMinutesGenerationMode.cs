using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddMinutesGenerationMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The new column defaults to 0 (SingleCall), which also backfills the seeded PlatformSettings row.
            // (EF scaffolds an empty UpdateData for the seed row here because its value equals the default -
            // that produces invalid `SET  WHERE` SQL, so it is intentionally omitted.)
            migrationBuilder.AddColumn<int>(
                name: "MinutesGenerationMode",
                table: "PlatformSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinutesGenerationMode",
                table: "PlatformSettings");
        }
    }
}
