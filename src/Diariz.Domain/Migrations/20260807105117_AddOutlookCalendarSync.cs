using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddOutlookCalendarSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OutlookSyncEnabled",
                table: "UserSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "OutlookCalendarSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DeviceName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    MailboxName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Color = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    PastDays = table.Column<int>(type: "integer", nullable: false),
                    FutureDays = table.Column<int>(type: "integer", nullable: false),
                    SkipPrivate = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeBody = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LastEventCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutlookCalendarSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutlookCalendarSources_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutlookCalendarEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Uid = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AllDay = table.Column<bool>(type: "boolean", nullable: false),
                    StartDate = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    EndDate = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    TimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    WindowsTimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Location = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    OnlineMeetingUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    BodyText = table.Column<string>(type: "text", nullable: true),
                    Categories = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Sensitivity = table.Column<int>(type: "integer", nullable: false),
                    BusyStatus = table.Column<int>(type: "integer", nullable: false),
                    IsRecurring = table.Column<bool>(type: "boolean", nullable: false),
                    OrganizerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OrganizerEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AttendeesJson = table.Column<string>(type: "jsonb", nullable: true),
                    SourceLastModified = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SyncId = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutlookCalendarEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutlookCalendarEvents_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OutlookCalendarEvents_OutlookCalendarSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "OutlookCalendarSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCalendarEvents_SourceId_StartsAt",
                table: "OutlookCalendarEvents",
                columns: new[] { "SourceId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCalendarEvents_SourceId_Uid",
                table: "OutlookCalendarEvents",
                columns: new[] { "SourceId", "Uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCalendarEvents_UserId_StartsAt",
                table: "OutlookCalendarEvents",
                columns: new[] { "UserId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCalendarSources_UserId",
                table: "OutlookCalendarSources",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCalendarSources_UserId_DeviceId",
                table: "OutlookCalendarSources",
                columns: new[] { "UserId", "DeviceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutlookCalendarEvents");

            migrationBuilder.DropTable(
                name: "OutlookCalendarSources");

            migrationBuilder.DropColumn(
                name: "OutlookSyncEnabled",
                table: "UserSettings");
        }
    }
}
