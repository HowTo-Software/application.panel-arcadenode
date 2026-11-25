using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServerPanel.API.Migrations
{
    /// <inheritdoc />
    public partial class ImportPterodactylEggs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Eggs",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500);

            migrationBuilder.AddColumn<string>(
                name: "PterodactylManifest",
                table: "Eggs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PterodactylSha256",
                table: "Eggs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Eggs_NestId_PterodactylSha256",
                table: "Eggs",
                columns: new[] { "NestId", "PterodactylSha256" },
                unique: true,
                filter: "[PterodactylSha256] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [Eggs] WHERE [PterodactylManifest] IS NOT NULL OR DATALENGTH([Description]) > 1000)
                    THROW 51000, 'Archive and remove imported eggs before rolling back the egg catalog schema.', 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Eggs_NestId_PterodactylSha256",
                table: "Eggs");

            migrationBuilder.DropColumn(
                name: "PterodactylManifest",
                table: "Eggs");

            migrationBuilder.DropColumn(
                name: "PterodactylSha256",
                table: "Eggs");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Eggs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(4000)",
                oldMaxLength: 4000);

        }
    }
}
