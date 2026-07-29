using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <summary>Turns the voiceprint table into the people directory: the embedding becomes optional and the
    /// contact fields arrive, so a person who has never been voice-printed is a first-class row.
    ///
    /// <para><b>Forward-restore-safe, so MaintenanceController.CurrentFormat stays 1.</b> Every operation here
    /// is an ADD COLUMN, an ALTER COLUMN ... DROP NOT NULL, an index add, or an FK add. There is no DropTable,
    /// CreateTable or RenameColumn - the CLR types were renamed to Person/VoiceSample but the tables stay
    /// SpeakerProfiles/ProfileContributions and the columns stay UserId/ProfileId, pinned by ToTable and
    /// HasColumnName in DiarizDbContext. Renaming them would reject every backup archive taken before this
    /// point, with no conversion path.</para></summary>
    public partial class AddPersonDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "SpeakerProfiles",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "RoomId",
                table: "SpeakerProfiles",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Vector>(
                name: "Embedding",
                table: "SpeakerProfiles",
                type: "vector(192)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(192)");

            migrationBuilder.AddColumn<string>(
                name: "CompanyName",
                table: "SpeakerProfiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "SpeakerProfiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsInternal",
                table: "SpeakerProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "LinkedUserId",
                table: "SpeakerProfiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "SpeakerProfiles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "SpeakerProfiles",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VoiceprintOptOut",
                table: "SpeakerProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_SpeakerProfiles_Email",
                table: "SpeakerProfiles",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_SpeakerProfiles_LinkedUserId",
                table: "SpeakerProfiles",
                column: "LinkedUserId",
                unique: true,
                filter: "\"LinkedUserId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_SpeakerProfiles_AspNetUsers_LinkedUserId",
                table: "SpeakerProfiles",
                column: "LinkedUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // Must run after the columns exist. Gives every existing active user their linked person, and
            // documents the UserId/LinkedUserId split in the database itself so it is visible from psql.
            migrationBuilder.Sql(PersonForUserBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SpeakerProfiles_AspNetUsers_LinkedUserId",
                table: "SpeakerProfiles");

            migrationBuilder.DropIndex(
                name: "IX_SpeakerProfiles_Email",
                table: "SpeakerProfiles");

            migrationBuilder.DropIndex(
                name: "IX_SpeakerProfiles_LinkedUserId",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "CompanyName",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "IsInternal",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "LinkedUserId",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "VoiceprintOptOut",
                table: "SpeakerProfiles");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "SpeakerProfiles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "RoomId",
                table: "SpeakerProfiles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Vector>(
                name: "Embedding",
                table: "SpeakerProfiles",
                type: "vector(192)",
                nullable: false,
                oldClrType: typeof(Vector),
                oldType: "vector(192)",
                oldNullable: true);
        }
    }
}
