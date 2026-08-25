using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddSpeakerSuggestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SuggestedAt",
                table: "Speakers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SuggestedDistance",
                table: "Speakers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SuggestedProfileId",
                table: "Speakers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_SuggestedProfileId",
                table: "Speakers",
                column: "SuggestedProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_Speakers_SpeakerProfiles_SuggestedProfileId",
                table: "Speakers",
                column: "SuggestedProfileId",
                principalTable: "SpeakerProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Speakers_SpeakerProfiles_SuggestedProfileId",
                table: "Speakers");

            migrationBuilder.DropIndex(
                name: "IX_Speakers_SuggestedProfileId",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "SuggestedAt",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "SuggestedDistance",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "SuggestedProfileId",
                table: "Speakers");
        }
    }
}
