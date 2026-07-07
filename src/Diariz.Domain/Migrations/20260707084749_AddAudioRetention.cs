using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AudioProtectedAt",
                table: "Recordings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "AudioDeletionTimeOfDay",
                table: "PlatformSettings",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(3, 0, 0));

            migrationBuilder.AddColumn<int>(
                name: "AudioRetentionDays",
                table: "PlatformSettings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<bool>(
                name: "AutoDeleteAudioEnabled",
                table: "PlatformSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "PlatformSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "AudioDeletionTimeOfDay", "AudioRetentionDays" },
                values: new object[] { new TimeOnly(3, 0, 0), 30 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioProtectedAt",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "AudioDeletionTimeOfDay",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "AudioRetentionDays",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "AutoDeleteAudioEnabled",
                table: "PlatformSettings");
        }
    }
}
