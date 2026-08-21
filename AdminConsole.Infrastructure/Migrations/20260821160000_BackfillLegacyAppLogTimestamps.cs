using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdminConsole.Infrastructure.Migrations
{
    /// <summary>
    /// Контрольна перевірка (2026-08-21): попередня міграція
    /// (FixAppLogTimestampSqliteOrdering) змінила ДЕКЛАРОВАНИЙ тип колонки
    /// Timestamp з TEXT на INTEGER, але SQLite AlterColumn — це rebuild
    /// таблиці на рівні SQL (CREATE temp -> INSERT SELECT * -> DROP -> RENAME),
    /// який копіює вже наявні рядки БУКВАЛЬНО, не пропускаючи їх через
    /// C#-конвертер (HasConversion діє лише на LINQ-запити EF Core, а не на
    /// сирий SQL міграції). Через відсутність строгої типізації колонок у
    /// SQLite (type affinity, не strict typing) старі рядки, записані ДО тієї
    /// міграції у форматі ISO-8601 TEXT з локальним офсетом (напр.
    /// "2026-08-21 18:25:48.381153+03:00"), так і лишились текстовими —
    /// вони мирно співіснують у тій самій колонці з новими INTEGER-рядками.
    ///
    /// Наслідок — двоякий:
    /// 1. ORDER BY Timestamp DESC (LogsController) сортує за SQLite
    ///    type-affinity рангом (TEXT > INTEGER), тож усі старі TEXT-рядки
    ///    ЗАВЖДИ спливають ПЕРШИМИ в "найновіші зверху" списку, незалежно
    ///    від реальної дати.
    /// 2. C#-конвертер на читання очікує `long` (UTC-тіки) — при спробі
    ///    прочитати TEXT-значення як long SQLite повертає скалярне число
    ///    близьке до нуля (не валідний парсинг), що конструктор
    ///    DateTimeOffset(ticks, TimeSpan.Zero) рендерить як "0001-01-01" —
    ///    саме симптом, який побачив користувач у UI.
    ///
    /// Фікс: одноразовий backfill — перепаковуємо будь-який рядок, чиє
    /// СИРЕ значення Timestamp має SQLite storage class, відмінний від
    /// 'integer', у коректні UTC-тіки. strftime('%s', Timestamp) коректно
    /// парсить формат "YYYY-MM-DD HH:MM:SS[.SSS][+HH:MM]" і сам нормалізує
    /// вказаний офсет у UTC (SQLite-специфічна поведінка часового модифікатора)
    /// — емпірично звірено проти DateTimeOffset.Parse() на реальних рядках
    /// цього ж проєкту (розбіжність <1с, лише втрата дробової частки секунди,
    /// прийнятно для історичних лог-записів). 621355968000000000 —
    /// DateTimeOffset.UnixEpoch.Ticks (тіки на 1970-01-01T00:00:00Z).
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
            // Одноразовий backfill даних — оригінальний TEXT-рядок з
            // мілісекундною точністю відновити неможливо (strftime('%s', ...)
            // у Up() втрачає дробову частку секунди), тож Down() свідомо
            // не намагається відкотити самі значення, лише документує факт.
        }
    }
}
