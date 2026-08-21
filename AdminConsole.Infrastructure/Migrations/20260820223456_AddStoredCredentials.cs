using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdminConsole.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStoredCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoredCredentials",
                columns: table => new
                {
                    Target = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedSecret = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredCredentials", x => x.Target);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoredCredentials");
        }
    }
}
