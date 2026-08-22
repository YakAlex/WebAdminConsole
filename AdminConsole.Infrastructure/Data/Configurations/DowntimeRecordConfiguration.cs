using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminConsole.Infrastructure.Data.Configurations;

/// <summary>
/// Natural key (ServerIp, FellAt) — the same one UptimeTrackerService already
/// uses for deduplication/lookup (LoadFromDisk, DeleteRecord). No synthetic
/// Id — the old monthly file grouping was purely a limitation of the old
/// JSON-based persistence, not a business rule.
/// </summary>
public sealed class DowntimeRecordConfiguration : IEntityTypeConfiguration<DowntimeRecord>
{
    public void Configure(EntityTypeBuilder<DowntimeRecord> builder)
    {
        builder.ToTable("DowntimeRecords");
        builder.HasKey(r => new { r.ServerIp, r.FellAt });

        builder.Property(r => r.ServerName).IsRequired();
        builder.Property(r => r.ServerIp).IsRequired();
        builder.Property(r => r.ServerGroup).IsRequired();

        // Computed [JsonIgnore] properties with no setter — not mapped.
        builder.Ignore(r => r.Duration);
        builder.Ignore(r => r.IsResolved);
        builder.Ignore(r => r.DurationDisplay);
    }
}
