using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminConsole.Infrastructure.Data.Configurations;

/// <summary>
/// Нова таблиця — заміна файлових app-YYYY-MM-DD.log (T4.13, FileLoggerService
/// видаляється). Formatted — обчислюваний get-only рядок (кешується при
/// конструюванні record'а), відновлюється автоматично з інших чотирьох полів
/// при кожному матеріалізованому конструюванні — не мапиться і не зберігається.
/// </summary>
public sealed class AppLogEntryConfiguration : IEntityTypeConfiguration<AppLogEntry>
{
    public void Configure(EntityTypeBuilder<AppLogEntry> builder)
    {
        builder.ToTable("AppLogEntries");

        builder.Property<int>("Id");
        builder.HasKey("Id");

        // SQLite provider не транслює ORDER BY по DateTimeOffset у SQL (не може
        // гарантувати коректність порівняння двох значень з різним offset).
        // Зберігаємо як UTC-тіки (long) — сортується/фільтрується на боці SQL,
        // момент часу (UTC-instant) зберігається без втрат.
        builder.Property(e => e.Timestamp)
            .HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero))
            .IsRequired();
        builder.HasIndex(e => e.Timestamp);

        builder.Ignore(e => e.Formatted);
    }
}
