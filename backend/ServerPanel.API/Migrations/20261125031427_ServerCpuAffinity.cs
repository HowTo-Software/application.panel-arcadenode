using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServerPanel.API.Migrations
{
    /// <inheritdoc />
    public partial class ServerCpuAffinity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CpuSet",
                table: "Servers",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CpuSet",
                table: "Servers");
        }
    }
}
