using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddSectionAttachmentUploader : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UploadedByUserId",
                table: "SectionAttachments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill: every existing row was charged to its folder's creator (Section.UserId), because
            // StorageUsage summed section attachments that way. That's who they're charged to today, so this is
            // a no-op in effect - nobody's usage jumps. New rows are stamped with the actual uploader going
            // forward (SectionAttachmentsController), which can differ from the folder's creator in a shared room.
            migrationBuilder.Sql("""
                UPDATE "SectionAttachments" AS sa
                SET "UploadedByUserId" = s."UserId"
                FROM "Sections" AS s
                WHERE s."Id" = sa."SectionId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SectionAttachments_UploadedByUserId",
                table: "SectionAttachments",
                column: "UploadedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SectionAttachments_UploadedByUserId",
                table: "SectionAttachments");

            migrationBuilder.DropColumn(
                name: "UploadedByUserId",
                table: "SectionAttachments");
        }
    }
}
