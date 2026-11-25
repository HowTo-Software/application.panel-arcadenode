using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServerPanel.API.Migrations
{
    /// <inheritdoc />
    public partial class GatePterodactylCutover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MigrationReleasedAt",
                table: "Servers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MigrationReleasedById",
                table: "Servers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MigrationSourceContainerId",
                table: "Servers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MigrationSourceStoppedAt",
                table: "Servers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MigrationVolumeVerifiedAt",
                table: "Servers",
                type: "datetime2",
                nullable: true);

            // A previously released migration must continue to support normal suspension and resume.
            migrationBuilder.Sql("""
                UPDATE [Servers]
                SET [MigrationReleasedAt] = COALESCE([LastStartedAt], SYSUTCDATETIME())
                WHERE [RestoredVolumeSha256] IS NOT NULL AND [MigrationPending] = 0
                  AND ([IsSuspended] = 0 OR [LastStartedAt] IS NOT NULL)
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MigrationReleasedAt",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "MigrationReleasedById",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "MigrationSourceContainerId",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "MigrationSourceStoppedAt",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "MigrationVolumeVerifiedAt",
                table: "Servers");
        }
    }
}
