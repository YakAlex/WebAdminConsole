using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminConsole.Infrastructure.Data.Configurations;

/// <summary>Single-row table — surrogate Id, the repository always reads/writes the first (and only) row.</summary>
public sealed class AppSettingsConfiguration : IEntityTypeConfiguration<AppSettings>
{
    public void Configure(EntityTypeBuilder<AppSettings> builder)
    {
        builder.ToTable("AppSettings");
        builder.Property<int>("Id");
        builder.HasKey("Id");
    }
}
