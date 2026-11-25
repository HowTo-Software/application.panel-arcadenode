using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServerPanel.API.Migrations
{
    /// <inheritdoc />
    public partial class ManageBackupDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeletionNodeId",
                table: "Backups",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletionStatus",
                table: "Backups",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Backups_DeletionStatus_CreatedAt",
                table: "Backups",
                columns: new[] { "DeletionStatus", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Backups_DeletionStatus_CreatedAt",
                table: "Backups");

            migrationBuilder.DropColumn(
                name: "DeletionNodeId",
                table: "Backups");

            migrationBuilder.DropColumn(
                name: "DeletionStatus",
                table: "Backups");
        }
    }
}
