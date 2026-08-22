using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminConsole.Infrastructure.Data.Configurations;

/// <summary>
/// BackupCheckState (1) → BackupSample (N), FK on BackupCheckStateId (T2.1).
/// Surrogate int PK instead of a composite (Name, Kind) — this makes it
/// simpler to declare the FK for the child History; (Name, Kind) remains a
/// unique index for lookups — this is exactly what BackupMonitorService uses
/// to find state by (StateKey = "Name|Kind").
///
/// History is an EF Core owned collection: BackupSample has no identity of
/// its own outside its parent (always read/written together with it) —
/// owned types exist for exactly this scenario, instead of a manual
/// shadow-FK/Id on every child record.
/// </summary>
public sealed class BackupCheckStateConfiguration : IEntityTypeConfiguration<BackupCheckState>
{
    public void Configure(EntityTypeBuilder<BackupCheckState> builder)
    {
        builder.ToTable("BackupCheckStates");

        builder.Property<int>("BackupCheckStateId");
        builder.HasKey("BackupCheckStateId");
        builder.HasIndex(s => new { s.Name, s.Kind }).IsUnique();

        builder.Property(s => s.Name).IsRequired();
        builder.Property(s => s.Host).IsRequired();

        builder.OwnsMany(s => s.History, history =>
        {
            history.ToTable("BackupSamples");

            // Id is the sole PK (single-column autoincrement, SQLite ROWID) instead
            // of a composite (BackupCheckStateId, Id): SQLite doesn't support generated
            // values in composite keys ("SQLite does not support generated
            // values on composite keys" — EF Core's model validator). The FK remains
            // a plain, non-key column.
            history.Property<int>("Id");
            history.HasKey("Id");
            history.WithOwner().HasForeignKey("BackupCheckStateId");

            history.Property(h => h.ObservedAt).IsRequired();
            history.Property(h => h.SizeBytes).IsRequired();
        });
    }
}
