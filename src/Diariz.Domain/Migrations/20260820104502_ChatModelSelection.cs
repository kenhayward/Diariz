using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <summary>Model display names, a chat-offered flag, and a user's chosen chat model.
    ///
    /// <b>Additive only</b>, so an older backup restores its own columns and this adds these on top -
    /// <c>MaintenanceController.CurrentFormat</c> is deliberately NOT bumped. Bumping it would hard-reject
    /// every pre-0.231.0 backup to guard against a change that cannot corrupt one.
    ///
    /// <b>No backfill of ChatEnabled.</b> ChatModelCatalog offers the chat-assigned model implicitly, so a
    /// platform upgraded with zero ChatEnabled rows behaves exactly as it did before. Writing rows here to
    /// say what the code already infers would be a one-way data move for no behavioural gain.
    ///
    /// The FK is <c>SET NULL</c>, unlike the RESTRICT on the routing table: a user's pick is a preference,
    /// and it must never be able to refuse an administrator's delete.</summary>
    public partial class ChatModelSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ChatModelId",
                table: "UserSettings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ChatEnabled",
                table: "LlmModels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "LlmModels",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSettings_ChatModelId",
                table: "UserSettings",
                column: "ChatModelId");

            migrationBuilder.AddForeignKey(
                name: "FK_UserSettings_LlmModels_ChatModelId",
                table: "UserSettings",
                column: "ChatModelId",
                principalTable: "LlmModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserSettings_LlmModels_ChatModelId",
                table: "UserSettings");

            migrationBuilder.DropIndex(
                name: "IX_UserSettings_ChatModelId",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ChatModelId",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ChatEnabled",
                table: "LlmModels");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "LlmModels");
        }
    }
}
