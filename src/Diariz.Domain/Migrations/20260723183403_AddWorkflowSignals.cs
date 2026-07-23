using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SignalFilter",
                table: "Webhooks",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkflowSignals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowSignals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FormulaWorkflowSignals",
                columns: table => new
                {
                    FormulaId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowSignalId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormulaWorkflowSignals", x => new { x.FormulaId, x.WorkflowSignalId });
                    table.ForeignKey(
                        name: "FK_FormulaWorkflowSignals_Formulas_FormulaId",
                        column: x => x.FormulaId,
                        principalTable: "Formulas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FormulaWorkflowSignals_WorkflowSignals_WorkflowSignalId",
                        column: x => x.WorkflowSignalId,
                        principalTable: "WorkflowSignals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FormulaWorkflowSignals_WorkflowSignalId",
                table: "FormulaWorkflowSignals",
                column: "WorkflowSignalId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowSignals_Key",
                table: "WorkflowSignals",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FormulaWorkflowSignals");

            migrationBuilder.DropTable(
                name: "WorkflowSignals");

            migrationBuilder.DropColumn(
                name: "SignalFilter",
                table: "Webhooks");
        }
    }
}
