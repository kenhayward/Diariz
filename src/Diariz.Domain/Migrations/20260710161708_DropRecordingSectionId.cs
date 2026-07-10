using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class DropRecordingSectionId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Recordings_Sections_SectionId",
                table: "Recordings");

            migrationBuilder.DropIndex(
                name: "IX_Recordings_SectionId",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "SectionId",
                table: "Recordings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SectionId",
                table: "Recordings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_SectionId",
                table: "Recordings",
                column: "SectionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Recordings_Sections_SectionId",
                table: "Recordings",
                column: "SectionId",
                principalTable: "Sections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
