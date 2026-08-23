using AdminConsole.Domain.Models;

namespace AdminConsole.Domain.Abstractions;

/// <summary>
/// Persistence for AppSettings (single-row) and the list of allowed
/// Telegram users. Mirrors UserSettingsService.Current/Save/
/// MutateTelegramState/ReadTelegramState (Phase 2, T2.3), decomposed
/// into concrete async operations instead of an Action&lt;T&gt; delegate —
/// a more natural fit for EF Core than a synchronous lock around mutating
/// an in-memory object.
/// </summary>
public interface IAppSettingsRepository
{
    /// <summary>Returns the single settings row (creates a default one if the DB is still empty).</summary>
    Task<AppSettings> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Full row overwrite — ONLY for a one-time initial write
    /// (AdminConsole.Migration, migrating from the legacy WPF UserSettings),
    /// where there are no concurrent callers. For any ongoing update of a
    /// single field, use the narrow Update*Async methods below — SaveAsync
    /// reads and rewrites ALL fields together, so a caller holding a stale
    /// copy of the object risks clobbering something another caller just
    /// changed (Zone 2 audit, Finding #2, 2026-08-22).
    /// </summary>
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>Targeted update of just the three monitoring toggles (Settings UI).</summary>
    Task UpdateMonitoringTogglesAsync(
        bool rdpEnabled, bool zabbixEnabled, bool backupEnabled, CancellationToken ct = default);

    /// <summary>Targeted update of just the RDP daily peak (RdpMonitorService).</summary>
    Task UpdateRdpDailyPeakAsync(int peak, DateTime date, CancellationToken ct = default);

    /// <summary>Targeted update of just the Telegram Primary Admin chat_id (claim-admin).</summary>
    Task UpdateTelegramPrimaryAdminAsync(long chatId, CancellationToken ct = default);

    /// <summary>Targeted update of just the minimum Zabbix severity to poll for (Settings UI).</summary>
    Task UpdateZabbixMinSeverityAsync(int minSeverity, CancellationToken ct = default);

    Task<IReadOnlyList<TelegramAllowedUser>> GetTelegramAllowedUsersAsync(CancellationToken ct = default);

    /// <summary>Insert or update by ChatId (approving a new user / refreshing the username on a repeat /start).</summary>
    Task UpsertTelegramAllowedUserAsync(long chatId, string? username, CancellationToken ct = default);

    /// <summary>Revoke — removes the user from the allowed list.</summary>
    Task RemoveTelegramAllowedUserAsync(long chatId, CancellationToken ct = default);
}
