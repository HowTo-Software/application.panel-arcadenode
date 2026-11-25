using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServerPanel.API.Migrations
{
    /// <inheritdoc />
    public partial class SecurityHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Abort before changing schema if legacy records cannot satisfy the new constraints.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [Nodes] WHERE LEN([DockerEndpoint]) > 450)
                    THROW 51000, 'Resolve Docker endpoints longer than 450 characters before migration.', 1;
                IF EXISTS (SELECT [DockerEndpoint] FROM [Nodes] GROUP BY [DockerEndpoint] HAVING COUNT(*) > 1)
                    THROW 51000, 'Resolve duplicate Docker endpoints before migration.', 1;
                IF EXISTS (SELECT [NodeId], [Port] FROM [Servers] GROUP BY [NodeId], [Port] HAVING COUNT(*) > 1)
                    THROW 51000, 'Resolve duplicate node/port reservations before migration.', 1;
                """);
            migrationBuilder.DropIndex(
                name: "IX_Servers_NodeId",
                table: "Servers");

            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "Users",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE [Users] SET [SecurityStamp] = REPLACE(CONVERT(nvarchar(36), NEWID()), '-', '');");
            // Old invitations used predictable Random and could be unlimited or nonexpiring.
            migrationBuilder.Sql("UPDATE [InviteCodes] SET [IsRevoked] = 1;");

            migrationBuilder.AlterColumn<string>(
                name: "DockerEndpoint",
                table: "Nodes",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<Guid>(
                name: "ReservationVersion",
                table: "Nodes",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyVersion",
                table: "InviteCodes",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "AuthSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Servers_NodeId_Port",
                table: "Servers",
                columns: new[] { "NodeId", "Port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_DockerEndpoint",
                table: "Nodes",
                column: "DockerEndpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_UserId_ExpiresAt",
                table: "AuthSessions",
                columns: new[] { "UserId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthSessions");

            migrationBuilder.DropIndex(
                name: "IX_Servers_NodeId_Port",
                table: "Servers");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_DockerEndpoint",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReservationVersion",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ConcurrencyVersion",
                table: "InviteCodes");

            migrationBuilder.AlterColumn<string>(
                name: "DockerEndpoint",
                table: "Nodes",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_NodeId",
                table: "Servers",
                column: "NodeId");
        }
    }
}
