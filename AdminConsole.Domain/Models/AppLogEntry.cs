namespace AdminConsole.Domain.Models;

public enum LogSeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// A single structured application log entry.
/// Immutable record produced by any service, consumed by
/// FileLoggerService (disk) and LogsViewModel (UI).
/// </summary>
public sealed record AppLogEntry(
    LogSeverity Severity,
    string      Source,
    string      Message,
    DateTimeOffset Timestamp
)
{
    /// <summary>
    /// Maximum length of a single log message. Guards against uncontrolled
    /// external input (e.g. arbitrary text from a Telegram message sent by
    /// an unauthorized user) inflating a single log line to an unbounded
    /// size.
    /// </summary>
    private const int MaxMessageLength = 2000;

    // Cached on first access — the record is immutable, so the value never changes.
    // Avoids re-formatting the string on every file write and UI render.
    //
    // Log Injection fix: Message may contain RAW, externally controlled
    // text (e.g. the contents of a Telegram message from anyone who has
    // messaged the bot — even an unauthorized user). Without sanitization,
    // \r\n characters could be used to fake the appearance of an ENTIRE
    // extra log line in the file — an attacker could impersonate a genuine
    // system entry. So we strip/escape line breaks and control characters
    // BEFORE the string ends up in Formatted.
    public string Formatted { get; } =
        $"[{Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}] [{Severity,-7}] [{Source}] {Sanitize(Message)}";

    private static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;

        // Replace any line breaks with a visible, safe marker — preserves
        // the information (rather than just deleting it) while preventing
        // a fake extra log line.
        var sb = new System.Text.StringBuilder(message.Length);
        foreach (char c in message)
        {
            switch (c)
            {
                case '\r': break; // dropped entirely — \n below already represents the break
                case '\n': sb.Append("⏎"); break;
                default:
                    // Other control characters (besides normal printable ones) are
                    // stripped too — guards against other forms of terminal/file
                    // injection (e.g. escape sequences).
                    if (char.IsControl(c)) continue;
                    sb.Append(c);
                    break;
            }
        }

        string result = sb.ToString();
        return result.Length > MaxMessageLength
            ? result[..MaxMessageLength] + "…(truncated)"
            : result;
    }
}
