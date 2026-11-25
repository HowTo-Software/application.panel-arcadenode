using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServerPanel.API.Migrations
{
    /// <inheritdoc />
    public partial class QueueNodeBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsQueued",
                table: "Backups",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Backups_IsQueued_CreatedAt",
                table: "Backups",
                columns: new[] { "IsQueued", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Backups_IsQueued_CreatedAt",
                table: "Backups");

            migrationBuilder.DropColumn(
                name: "IsQueued",
                table: "Backups");
        }
    }
}
