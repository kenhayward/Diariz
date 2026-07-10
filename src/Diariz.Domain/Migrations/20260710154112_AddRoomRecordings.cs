using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomRecordings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoomRecordings",
                columns: table => new
                {
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsMainRoom = table.Column<bool>(type: "boolean", nullable: false),
                    SectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SharedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SharedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomRecordings", x => new { x.RoomId, x.RecordingId });
                    table.CheckConstraint("CK_RoomRecordings_MainRoomHasNoSharer", "NOT \"IsMainRoom\" OR (\"SharedByUserId\" IS NULL AND \"SharedAt\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_RoomRecordings_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoomRecordings_Rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "Rooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoomRecordings_Sections_SectionId",
                        column: x => x.SectionId,
                        principalTable: "Sections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoomRecordings_RecordingId",
                table: "RoomRecordings",
                column: "RecordingId",
                unique: true,
                filter: "\"IsMainRoom\"");

            migrationBuilder.CreateIndex(
                name: "IX_RoomRecordings_RoomId_SectionId",
                table: "RoomRecordings",
                columns: new[] { "RoomId", "SectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoomRecordings_SectionId",
                table: "RoomRecordings",
                column: "SectionId");

            // One main placement per existing recording, in its recorder's personal room, keeping its folder.
            // Mints any missing personal room first. Runs exactly once per database - see
            // RecordingPlacementBackfill.
            migrationBuilder.Sql(RecordingPlacementBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoomRecordings");
        }
    }
}
