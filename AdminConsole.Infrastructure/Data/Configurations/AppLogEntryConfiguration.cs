using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminConsole.Infrastructure.Data.Configurations;

/// <summary>
/// New table — replaces the file-based app-YYYY-MM-DD.log files (T4.13,
/// FileLoggerService is being removed). Formatted is a computed get-only
/// string (cached when the record is constructed), rebuilt automatically
/// from the other four fields on every materialization — it is not mapped
/// or persisted.
/// </summary>
public sealed class AppLogEntryConfiguration : IEntityTypeConfiguration<AppLogEntry>
{
    public void Configure(EntityTypeBuilder<AppLogEntry> builder)
    {
        builder.ToTable("AppLogEntries");

        builder.Property<int>("Id");
        builder.HasKey("Id");

        // The SQLite provider doesn't translate ORDER BY over DateTimeOffset into
        // SQL (it can't guarantee correct comparison of two values with different
        // offsets). We store it as UTC ticks (long) — sortable/filterable on the
        // SQL side, and the UTC instant is preserved without loss.
        builder.Property(e => e.Timestamp)
            .HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero))
            .IsRequired();
        builder.HasIndex(e => e.Timestamp);

        builder.Ignore(e => e.Formatted);
    }
}
