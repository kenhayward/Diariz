using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddScreenshotOcr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OcrGeneratedAt",
                table: "MeetingScreenshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OcrModel",
                table: "MeetingScreenshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OcrText",
                table: "MeetingScreenshots",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OcrGeneratedAt",
                table: "MeetingScreenshots");

            migrationBuilder.DropColumn(
                name: "OcrModel",
                table: "MeetingScreenshots");

            migrationBuilder.DropColumn(
                name: "OcrText",
                table: "MeetingScreenshots");
        }
    }
}
