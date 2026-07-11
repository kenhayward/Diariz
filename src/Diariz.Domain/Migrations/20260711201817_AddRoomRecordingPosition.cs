using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomRecordingPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "RoomRecordings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Carry each recording's existing manual order onto its main placement; shared placements stay 0.
            migrationBuilder.Sql(RoomRecordingPositionBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Position",
                table: "RoomRecordings");
        }
    }
}
