using AdminConsole.Domain.Models;

namespace AdminConsole.Domain.Abstractions;

/// <summary>
/// Персистентність для AppSettings (single-row) і списку дозволених
/// Telegram-користувачів. Дзеркалить UserSettingsService.Current/Save/
/// MutateTelegramState/ReadTelegramState (Фаза 2, T2.3), декомпозоване
/// у конкретні асинхронні операції замість Action&lt;T&gt;-делегата —
/// природніше для EF Core, ніж синхронний lock навколо мутації in-memory
/// об'єкта.
/// </summary>
public interface IAppSettingsRepository
{
    /// <summary>Повертає єдиний рядок налаштувань (створює дефолтний, якщо БД ще порожня).</summary>
    Task<AppSettings> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Повний перезапис рядка — ЛИШЕ для одноразового первинного запису
    /// (AdminConsole.Migration, перенесення з legacy WPF UserSettings), де
    /// конкурентних викликачів немає. Для будь-якого поточного оновлення
    /// окремого поля використовуйте вузькі Update*Async нижче — SaveAsync
    /// читає й перезаписує ВСІ поля разом, тож викликач із застарілою
    /// копією об'єкта ризикує затерти те, що хтось інший щойно змінив
    /// (Аудит Зона 2, Знахідка №2, 2026-08-22).
    /// </summary>
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>Точкове оновлення лише трьох monitoring-перемикачів (Settings UI).</summary>
    Task UpdateMonitoringTogglesAsync(
        bool rdpEnabled, bool zabbixEnabled, bool backupEnabled, CancellationToken ct = default);

    /// <summary>Точкове оновлення лише RDP daily peak (RdpMonitorService).</summary>
    Task UpdateRdpDailyPeakAsync(int peak, DateTime date, CancellationToken ct = default);

    /// <summary>Точкове оновлення лише Telegram Primary Admin chat_id (claim-admin).</summary>
    Task UpdateTelegramPrimaryAdminAsync(long chatId, CancellationToken ct = default);

    Task<IReadOnlyList<TelegramAllowedUser>> GetTelegramAllowedUsersAsync(CancellationToken ct = default);

    /// <summary>Insert або update за ChatId (approve нового користувача / оновлення username при повторному /start).</summary>
    Task UpsertTelegramAllowedUserAsync(long chatId, string? username, CancellationToken ct = default);

    /// <summary>Revoke — видаляє користувача зі списку дозволених.</summary>
    Task RemoveTelegramAllowedUserAsync(long chatId, CancellationToken ct = default);
}
