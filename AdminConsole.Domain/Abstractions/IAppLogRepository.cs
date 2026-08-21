using AdminConsole.Domain.Models;

namespace AdminConsole.Domain.Abstractions;

/// <summary>
/// Персистентність для AppLogEntry — заміна файлових app-YYYY-MM-DD.log
/// (FileLoggerService, T4.13: сервіс видаляється повністю). Прибирає весь
/// клас проблем із multi-file merge/"хвіст найновішого файлу" — це просто
/// ORDER BY Timestamp DESC LIMIT :take в SQL (T3.9: GET /api/logs).
/// </summary>
public interface IAppLogRepository
{
    Task AppendAsync(AppLogEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Останні записи, найновіші перші. <paramref name="before"/>/<paramref name="after"/> —
    /// межі діапазону дат (Крок 6, #10 — пошук+фільтрація за датою на Logs),
    /// <paramref name="search"/> — підрядок у Source/Message (case-insensitive
    /// через SQLite LIKE за замовчуванням).
    /// </summary>
    Task<IReadOnlyList<AppLogEntry>> GetRecentAsync(
        int take,
        DateTimeOffset? before = null,
        DateTimeOffset? after  = null,
        string?         search = null,
        CancellationToken ct   = default);
}
