using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServerPanel.API.Migrations
{
    /// <inheritdoc />
    public partial class QueueBackupRestores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RestoreCompletedAt",
                table: "Backups",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RestoreNodeId",
                table: "Backups",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RestoreRequestedAt",
                table: "Backups",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RestoreStatus",
                table: "Backups",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "RestoredBytes",
                table: "Backups",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RestoredDirectories",
                table: "Backups",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RestoredFiles",
                table: "Backups",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Backups_RestoreStatus_RestoreRequestedAt",
                table: "Backups",
                columns: new[] { "RestoreStatus", "RestoreRequestedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Backups_RestoreStatus_RestoreRequestedAt",
                table: "Backups");

            migrationBuilder.DropColumn(
                name: "RestoreCompletedAt",
                table: "Backups");

            migrationBuilder.DropColumn(
                name: "RestoreNodeId",
                table: "Backups");

            migrationBuilder.DropColumn(
                name: "RestoreRequestedAt",
                table: "Backups");

            migrationBuilder.DropColumn(
                name: "RestoreStatus",
                table: "Backups");

            migrationBuilder.DropColumn(
                name: "RestoredBytes",
                table: "Backups");

            migrationBuilder.DropColumn(
                name: "RestoredDirectories",
                table: "Backups");

            migrationBuilder.DropColumn(
                name: "RestoredFiles",
                table: "Backups");
        }
    }
}
