using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MeetingTypeId",
                table: "Recordings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MeetingTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GroupName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Overview = table.Column<string>(type: "text", nullable: false),
                    Icon = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Color = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContentJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeetingTypes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_MeetingTypeId",
                table: "Recordings",
                column: "MeetingTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingTypes_Key",
                table: "MeetingTypes",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingTypes_UserId",
                table: "MeetingTypes",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Recordings_MeetingTypes_MeetingTypeId",
                table: "Recordings",
                column: "MeetingTypeId",
                principalTable: "MeetingTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Recordings_MeetingTypes_MeetingTypeId",
                table: "Recordings");

            migrationBuilder.DropTable(
                name: "MeetingTypes");

            migrationBuilder.DropIndex(
                name: "IX_Recordings_MeetingTypeId",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "MeetingTypeId",
                table: "Recordings");
        }
    }
}
