using AdminConsole.Domain.Models;

namespace AdminConsole.Domain.Abstractions;

/// <summary>
/// Персистентність для DowntimeRecord. Дзеркалить LoadFromDisk/SaveToDisk/
/// GetSnapshot з WPF UptimeTrackerService (Фаза 2, T2.3: "репозиторій
/// замість File I/O" — уся бізнес-логіка анти-флапінгу, reconciliation
/// і дедуплікації лишається в сервісі-споживачі БЕЗ ЗМІН, тут — лише
/// нижній шар збереження).
///
/// На відміну від файлового LoadFromDisk/SaveToDisk (які працювали
/// поверх місячних JSON-файлів і вимагали ручного групування/видалення
/// порожніх файлів), реляційна модель дозволяє прості CRUD-операції
/// за природним ключем (ServerIp, FellAt) — групування по місяцю було
/// суто файловим обмеженням, а не бізнес-правилом.
/// </summary>
public interface IDowntimeRepository
{
    /// <summary>Завантажує всі записи (відкриті й закриті) — виклик у конструкторі сервіса, до підписки на події.</summary>
    Task<IReadOnlyList<DowntimeRecord>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Insert або update за ключем (ServerIp, FellAt).</summary>
    Task UpsertAsync(DowntimeRecord record, CancellationToken ct = default);

    /// <summary>Видаляє один запис за природним ключем (DeleteRecord).</summary>
    Task DeleteAsync(string serverIp, DateTimeOffset fellAt, CancellationToken ct = default);

    /// <summary>Масове видалення всіх закритих (IsResolved) записів (ClearAllResolved). Повертає кількість видалених.</summary>
    Task<int> DeleteAllResolvedAsync(CancellationToken ct = default);
}
