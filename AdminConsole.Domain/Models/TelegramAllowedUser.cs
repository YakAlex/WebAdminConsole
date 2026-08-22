namespace AdminConsole.Domain.Models;

/// <summary>
/// An allowed user of the Telegram bot (read-only access, granted by the
/// Primary Admin). Replaces two parallel WPF UserSettings collections —
/// TelegramAllowedChatIds (List&lt;long&gt;) and TelegramUsernames
/// (Dictionary&lt;long, string&gt;) — with a single table (Phase 2).
/// </summary>
public sealed class TelegramAllowedUser
{
    public long ChatId { get; set; }

    /// <summary>
    /// The last known Telegram username (without @). Updated on approve
    /// and on every /start from an already-allowed user.
    /// </summary>
    public string? Username { get; set; }
}
