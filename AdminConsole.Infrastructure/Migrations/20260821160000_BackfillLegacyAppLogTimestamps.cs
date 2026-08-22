using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdminConsole.Infrastructure.Migrations
{
    /// <summary>
    /// Follow-up fix (2026-08-21): the previous migration
    /// (FixAppLogTimestampSqliteOrdering) changed the DECLARED type of the
    /// Timestamp column from TEXT to INTEGER, but SQLite's AlterColumn is a
    /// SQL-level table rebuild (CREATE temp -> INSERT SELECT * -> DROP ->
    /// RENAME) that copies existing rows LITERALLY, without running them
    /// through the C# converter (HasConversion only applies to EF Core LINQ
    /// queries, not to raw migration SQL). Because SQLite columns aren't
    /// strictly typed (type affinity, not strict typing), rows written
    /// BEFORE that migration in ISO-8601 TEXT format with a local offset
    /// (e.g. "2026-08-21 18:25:48.381153+03:00") stayed textual — they
    /// peacefully coexist in the same column alongside the new INTEGER rows.
    ///
    /// The consequence is twofold:
    /// 1. ORDER BY Timestamp DESC (LogsController) sorts by SQLite's
    ///    type-affinity rank (TEXT > INTEGER), so all the old TEXT rows
    ///    ALWAYS float to the TOP of the "newest first" list, regardless of
    ///    their actual date.
    /// 2. The C# read-side converter expects a `long` (UTC ticks) — trying
    ///    to read a TEXT value as a long, SQLite returns a scalar close to
    ///    zero (not a valid parse), which the DateTimeOffset(ticks,
    ///    TimeSpan.Zero) constructor renders as "0001-01-01" — exactly the
    ///    symptom the user saw in the UI.
    ///
    /// Fix: a one-time backfill — repack any row whose RAW Timestamp value
    /// has a SQLite storage class other than 'integer' into proper UTC
    /// ticks. strftime('%s', Timestamp) correctly parses the
    /// "YYYY-MM-DD HH:MM:SS[.SSS][+HH:MM]" format and normalizes the given
    /// offset to UTC on its own (SQLite-specific behavior of the time
    /// modifier) — verified empirically against DateTimeOffset.Parse() on
    /// real rows from this same project (discrepancy <1s, only the
    /// fractional-second part is lost, acceptable for historical log
    /// entries). 621355968000000000 is DateTimeOffset.UnixEpoch.Ticks
    /// (ticks at 1970-01-01T00:00:00Z).
    /// </summary>
    public partial class BackfillLegacyAppLogTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE AppLogEntries
                SET Timestamp = CAST(strftime('%s', Timestamp) AS INTEGER) * 10000000 + 621355968000000000
                WHERE typeof(Timestamp) != 'integer';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A one-time data backfill — the original TEXT row with
            // millisecond precision can't be recovered (strftime('%s', ...)
            // in Up() loses the fractional-second part), so Down()
            // deliberately doesn't try to roll back the values themselves,
            // it just documents that fact.
        }
    }
}
