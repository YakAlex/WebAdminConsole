using AdminConsole.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminConsole.Infrastructure.Data.Configurations;

public sealed class MigrationMarkerConfiguration : IEntityTypeConfiguration<MigrationMarker>
{
    public void Configure(EntityTypeBuilder<MigrationMarker> builder)
    {
        builder.ToTable("MigrationMarker");
        builder.HasKey(m => m.Id);

        // SQLite allows multiple NULLs in a unique index (they're never
        // considered equal to each other) — the pre-existing legacy row
        // (Step == null) coexists fine alongside the four new named rows.
        builder.HasIndex(m => m.Step).IsUnique();
    }
}
