using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddVoiceSampleRecomputeState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecomputeFailedAt",
                table: "ProfileContributions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecomputeQueuedAt",
                table: "ProfileContributions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecomputeFailedAt",
                table: "ProfileContributions");

            migrationBuilder.DropColumn(
                name: "RecomputeQueuedAt",
                table: "ProfileContributions");
        }
    }
}
