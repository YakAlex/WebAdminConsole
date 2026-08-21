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

    Task SaveAsync(AppSettings settings, CancellationToken ct = default);

    Task<IReadOnlyList<TelegramAllowedUser>> GetTelegramAllowedUsersAsync(CancellationToken ct = default);

    /// <summary>Insert або update за ChatId (approve нового користувача / оновлення username при повторному /start).</summary>
    Task UpsertTelegramAllowedUserAsync(long chatId, string? username, CancellationToken ct = default);

    /// <summary>Revoke — видаляє користувача зі списку дозволених.</summary>
    Task RemoveTelegramAllowedUserAsync(long chatId, CancellationToken ct = default);
}
