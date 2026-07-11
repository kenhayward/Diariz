using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomScopedEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RoomId",
                table: "SpeakerProfiles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "RoomId",
                table: "MeetingTypes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RoomId",
                table: "ChatSessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Put voiceprints, chats and personal meeting types in their owner's personal room (platform types
            // stay null). No FK yet - it lands with the UserId retirement in Phase 4. Once per DB.
            migrationBuilder.Sql(Diariz.Domain.Migrations.RoomScopedEntitiesBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RoomId",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "RoomId",
                table: "MeetingTypes");

            migrationBuilder.DropColumn(
                name: "RoomId",
                table: "ChatSessions");
        }
    }
}
