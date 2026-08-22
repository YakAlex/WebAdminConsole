namespace AdminConsole.Infrastructure.Telegram;

/// <summary>
/// Splits a list of ready-made text lines into "pages" under the Telegram
/// sendMessage/editMessageText limit (4096 characters). We keep a safety
/// margin (maxChars = 3500) — the page header + emoji take up space, better
/// to have headroom than hit a BadRequest from the API.
///
/// Carried over unchanged (T5.3).
/// </summary>
public static class TelegramTextChunker
{
    private const int DefaultMaxChars = 3500;

    /// <summary>
    /// Combines lines into pages so that no page exceeds maxChars. A single
    /// line is never split in half — if a line by itself is longer than
    /// maxChars, it's truncated with "…".
    /// </summary>
    public static List<string> BuildPages(
        IReadOnlyList<string> lines,
        string                header    = "",
        int                   maxChars  = DefaultMaxChars)
    {
        var pages   = new List<string>();
        var current = new System.Text.StringBuilder(header);
        if (header.Length > 0) current.Append('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Length > maxChars
                ? rawLine[..(maxChars - 1)] + "…"
                : rawLine;

            // +1 for \n
            if (current.Length + line.Length + 1 > maxChars && current.Length > header.Length)
            {
                pages.Add(current.ToString().TrimEnd());
                current = new System.Text.StringBuilder(header);
                if (header.Length > 0) current.Append('\n');
            }

            current.Append(line).Append('\n');
        }

        pages.Add(current.ToString().TrimEnd());
        return pages.Count == 0 ? [header] : pages;
    }
}
