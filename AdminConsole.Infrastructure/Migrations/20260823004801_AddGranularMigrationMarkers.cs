using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdminConsole.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGranularMigrationMarkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Step",
                table: "MigrationMarker",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MigrationMarker_Step",
                table: "MigrationMarker",
                column: "Step",
                unique: true);

            // Bug fix (2026-08-23, audit Finding 1.1): if a legacy marker row
            // (Step IS NULL) shows the migration was already fully completed
            // before this schema change, seed all four new per-step markers
            // as completed too — otherwise an already-migrated production
            // database would look like it needs to re-run every step from
            // scratch the next time the tool is invoked.
            // SQLite has no "AS alias(column-name)" derived-table syntax
            // (unlike Postgres/standard SQL) — the step names are named via
            // the SELECT itself instead of a VALUES(...) table alias.
            migrationBuilder.Sql("""
                INSERT INTO MigrationMarker (Step, CompletedAtUtc)
                SELECT v.step, m.CompletedAtUtc
                FROM (
                    SELECT 'Downtime' AS step
                    UNION ALL SELECT 'Maintenance'
                    UNION ALL SELECT 'Backups'
                    UNION ALL SELECT 'UserSettings'
                ) AS v, MigrationMarker m
                WHERE m.Step IS NULL AND m.CompletedAtUtc IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MigrationMarker_Step",
                table: "MigrationMarker");

            migrationBuilder.DropColumn(
                name: "Step",
                table: "MigrationMarker");
        }
    }
}
