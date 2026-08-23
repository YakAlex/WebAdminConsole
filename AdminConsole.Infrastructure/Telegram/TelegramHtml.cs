namespace AdminConsole.Infrastructure.Telegram;

/// <summary>
/// Escaping for Telegram's HTML parse mode. Any string that isn't a literal
/// written by us — Telegram usernames (fully attacker-controlled), server
/// names/reasons from appsettings.json — must go through this before being
/// interpolated into a message, or a stray '&lt;'/'&gt;'/'&amp;' breaks the
/// HTML (Telegram rejects the whole message with a 400) or, worse, lets a
/// hostile username close an open tag early and inject its own markup.
/// </summary>
public static class TelegramHtml
{
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // '&' must be replaced FIRST — otherwise the '&' just introduced by
        // escaping '<'/'>' would itself get escaped into "&amp;lt;".
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
