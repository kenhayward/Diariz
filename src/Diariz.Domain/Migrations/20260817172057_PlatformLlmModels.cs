using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class PlatformLlmModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DefaultLlmModelId",
                table: "PlatformSettings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LlmModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ApiBase = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ApiKeyEncrypted = table.Column<string>(type: "text", nullable: true),
                    ContextLength = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LlmCallAssignments",
                columns: table => new
                {
                    Group = table.Column<int>(type: "integer", nullable: false),
                    LlmModelId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmCallAssignments", x => x.Group);
                    table.ForeignKey(
                        name: "FK_LlmCallAssignments_LlmModels_LlmModelId",
                        column: x => x.LlmModelId,
                        principalTable: "LlmModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LlmModelParameters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LlmModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Group = table.Column<int>(type: "integer", nullable: false),
                    ParametersJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmModelParameters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LlmModelParameters_LlmModels_LlmModelId",
                        column: x => x.LlmModelId,
                        principalTable: "LlmModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "PlatformSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "DefaultLlmModelId",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformSettings_DefaultLlmModelId",
                table: "PlatformSettings",
                column: "DefaultLlmModelId");

            migrationBuilder.CreateIndex(
                name: "IX_LlmCallAssignments_LlmModelId",
                table: "LlmCallAssignments",
                column: "LlmModelId");

            migrationBuilder.CreateIndex(
                name: "IX_LlmModelParameters_LlmModelId_Group",
                table: "LlmModelParameters",
                columns: new[] { "LlmModelId", "Group" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LlmModels_Name",
                table: "LlmModels",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PlatformSettings_LlmModels_DefaultLlmModelId",
                table: "PlatformSettings",
                column: "DefaultLlmModelId",
                principalTable: "LlmModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlatformSettings_LlmModels_DefaultLlmModelId",
                table: "PlatformSettings");

            migrationBuilder.DropTable(
                name: "LlmCallAssignments");

            migrationBuilder.DropTable(
                name: "LlmModelParameters");

            migrationBuilder.DropTable(
                name: "LlmModels");

            migrationBuilder.DropIndex(
                name: "IX_PlatformSettings_DefaultLlmModelId",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "DefaultLlmModelId",
                table: "PlatformSettings");
        }
    }
}
