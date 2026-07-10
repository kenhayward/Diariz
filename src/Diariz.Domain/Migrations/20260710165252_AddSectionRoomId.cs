using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddSectionRoomId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RoomId",
                table: "Sections",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Put every section in its owner's personal room (minting a missing one first). No FK yet - it
            // lands with the UserId drop, once every fixture sets a room. Runs once per DB - see
            // SectionRoomBackfill.
            migrationBuilder.Sql(Diariz.Domain.Migrations.SectionRoomBackfill.Sql);

            migrationBuilder.CreateIndex(
                name: "IX_Sections_RoomId_Name",
                table: "Sections",
                columns: new[] { "RoomId", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sections_RoomId_Name",
                table: "Sections");

            migrationBuilder.DropColumn(
                name: "RoomId",
                table: "Sections");
        }
    }
}
