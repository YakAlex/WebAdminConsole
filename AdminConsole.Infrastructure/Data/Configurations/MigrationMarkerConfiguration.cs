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
    }
}
