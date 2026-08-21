namespace AdminConsole.Domain.Models;

/// <summary>
/// Дозволений користувач Telegram-бота (read-only доступ, наданий Primary
/// Admin). Заміна двох паралельних колекцій WPF UserSettings —
/// TelegramAllowedChatIds (List&lt;long&gt;) і TelegramUsernames
/// (Dictionary&lt;long, string&gt;) — однією таблицею (Фаза 2).
/// </summary>
public sealed class TelegramAllowedUser
{
    public long ChatId { get; set; }

    /// <summary>
    /// Останній відомий Telegram username (без @). Оновлюється при approve
    /// і при кожному /start вже дозволеного користувача.
    /// </summary>
    public string? Username { get; set; }
}
