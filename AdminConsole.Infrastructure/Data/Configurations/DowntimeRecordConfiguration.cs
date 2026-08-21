using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminConsole.Infrastructure.Data.Configurations;

/// <summary>
/// Природний ключ (ServerIp, FellAt) — той самий, яким UptimeTrackerService
/// уже користується для дедуплікації/пошуку (LoadFromDisk, DeleteRecord).
/// Жодного синтетичного Id — групування по місяцю файлів було суто
/// файловим обмеженням старої JSON-персистентності, не бізнес-правилом.
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

        // Обчислювані [JsonIgnore]-властивості без сеттера — не мапляться.
        builder.Ignore(r => r.Duration);
        builder.Ignore(r => r.IsResolved);
        builder.Ignore(r => r.DurationDisplay);
    }
}
