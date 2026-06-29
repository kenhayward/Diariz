using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddSectionParentAndPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "Sections",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "Sections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Sections_ParentId",
                table: "Sections",
                column: "ParentId");

            migrationBuilder.AddForeignKey(
                name: "FK_Sections_Sections_ParentId",
                table: "Sections",
                column: "ParentId",
                principalTable: "Sections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sections_Sections_ParentId",
                table: "Sections");

            migrationBuilder.DropIndex(
                name: "IX_Sections_ParentId",
                table: "Sections");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "Sections");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "Sections");
        }
    }
}
