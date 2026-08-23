using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdminConsole.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddZabbixMinSeverity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ZabbixMinSeverity",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 4);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ZabbixMinSeverity",
                table: "AppSettings");
        }
    }
}
