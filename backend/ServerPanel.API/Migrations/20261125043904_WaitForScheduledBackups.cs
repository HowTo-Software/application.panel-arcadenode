using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServerPanel.API.Migrations
{
    /// <inheritdoc />
    public partial class WaitForScheduledBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Backups_IsQueued_CreatedAt",
                table: "Backups");

            migrationBuilder.AddColumn<Guid>(
                name: "PendingBackupId",
                table: "Schedules",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PendingBackupTaskId",
                table: "Schedules",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProcessingStartedAt",
                table: "Schedules",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "Backups",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_IsProcessing_PendingBackupId",
                table: "Schedules",
                columns: new[] { "IsProcessing", "PendingBackupId" });

            migrationBuilder.CreateIndex(
                name: "IX_Backups_IsQueued_NextAttemptAt_CreatedAt",
                table: "Backups",
                columns: new[] { "IsQueued", "NextAttemptAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Schedules_IsProcessing_PendingBackupId",
                table: "Schedules");

            migrationBuilder.DropIndex(
                name: "IX_Backups_IsQueued_NextAttemptAt_CreatedAt",
                table: "Backups");

            migrationBuilder.DropColumn(
                name: "PendingBackupId",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "PendingBackupTaskId",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "ProcessingStartedAt",
                table: "Schedules");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "Backups");

            migrationBuilder.CreateIndex(
                name: "IX_Backups_IsQueued_CreatedAt",
                table: "Backups",
                columns: new[] { "IsQueued", "CreatedAt" });
        }
    }
}
