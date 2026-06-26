using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAccessFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FullName",
                table: "AspNetUsers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            // Existing users (incl. the seed admin) must stay enabled and Active — a false/0 default
            // would lock everyone out. New request-access users explicitly set Status=Requested.
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 2); // UserStatus.Active
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FullName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "IsEnabled",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "AspNetUsers");
        }
    }
}
