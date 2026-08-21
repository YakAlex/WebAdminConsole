namespace AdminConsole.Infrastructure.Data.Entities;

/// <summary>
/// Службовий прапорець "одноразова міграція з JSON вже виконана".
/// AdminConsole.Migration перевіряє це перед записом, щоб повторний
/// запуск не задублював дані (Фаза 2, T2.6). Інфраструктурна сутність —
/// не має бізнес-значення поза персистентністю, тому не в AdminConsole.Domain.
/// </summary>
public sealed class MigrationMarker
{
    public int Id { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
