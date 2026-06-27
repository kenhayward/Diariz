using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddSpeakerIdentification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Vector>(
                name: "Embedding",
                table: "Speakers",
                type: "vector(192)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IdentifiedAuto",
                table: "Speakers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ProfileId",
                table: "Speakers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SpeakerProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(192)", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeakerProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpeakerProfiles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileContributions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpeakerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(192)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileContributions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileContributions_SpeakerProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "SpeakerProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProfileContributions_Speakers_SpeakerId",
                        column: x => x.SpeakerId,
                        principalTable: "Speakers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Speakers_ProfileId",
                table: "Speakers",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileContributions_ProfileId",
                table: "ProfileContributions",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileContributions_SpeakerId",
                table: "ProfileContributions",
                column: "SpeakerId");

            migrationBuilder.CreateIndex(
                name: "IX_SpeakerProfiles_UserId",
                table: "SpeakerProfiles",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Speakers_SpeakerProfiles_ProfileId",
                table: "Speakers",
                column: "ProfileId",
                principalTable: "SpeakerProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Speakers_SpeakerProfiles_ProfileId",
                table: "Speakers");

            migrationBuilder.DropTable(
                name: "ProfileContributions");

            migrationBuilder.DropTable(
                name: "SpeakerProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Speakers_ProfileId",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "Embedding",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "IdentifiedAuto",
                table: "Speakers");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "Speakers");
        }
    }
}
