using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diariz.Domain.Migrations
{
    /// <inheritdoc />
    public partial class SyncPersonalRoomNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only: correct the Personal room names that drifted before the rename sync existed. No
            // schema change, so this migration is otherwise empty - and it is NOT destructive, so
            // MaintenanceController.CurrentFormat stays where it is and older backups still restore.
            migrationBuilder.Sql(PersonalRoomNameBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the old names were simply wrong, and the correct ones are re-derivable.
        }
    }
}
