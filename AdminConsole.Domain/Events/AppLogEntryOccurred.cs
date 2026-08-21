using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Публікується будь-яким сервісом, що хоче додати структурований запис
/// у застосунковий лог (вкладка Logs + AppLogEntries у БД).
/// Заміна AppLogEntryMessage (WeakReferenceMessenger) на MediatR.
/// </summary>
public sealed record AppLogEntryOccurred(AppLogEntry Entry) : INotification
{
    // ── Convenience factories so call-sites stay readable ──────────────────

    public static AppLogEntryOccurred Info(string source, string message) =>
        new(new AppLogEntry(LogSeverity.Info, source, message, DateTimeOffset.Now));

    public static AppLogEntryOccurred Success(string source, string message) =>
        new(new AppLogEntry(LogSeverity.Success, source, message, DateTimeOffset.Now));

    public static AppLogEntryOccurred Warning(string source, string message) =>
        new(new AppLogEntry(LogSeverity.Warning, source, message, DateTimeOffset.Now));

    public static AppLogEntryOccurred Error(string source, string message) =>
        new(new AppLogEntry(LogSeverity.Error, source, message, DateTimeOffset.Now));
}
