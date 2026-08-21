using AdminConsole.Domain.Models;

namespace AdminConsole.Domain.Abstractions;

/// <summary>
/// Персистентність для BackupCheckState (+ вкладена History з BackupSample).
/// Дзеркалить LoadFromDisk/SaveToDisk/GetSnapshot з WPF BackupMonitorService
/// (Фаза 2, T2.3). Ключ запису — (Name, Kind), той самий StateKey, що вже
/// використовує сервіс для дедуплікації/пошуку.
///
/// Реалізація в Infrastructure відповідає за коректне збереження вкладеної
/// History (1→N, FK BackupCheckStateId, з обрізанням до MaxHistorySamples —
/// ця межа лишається відповідальністю сервіса-споживача, не репозиторію).
/// </summary>
public interface IBackupStateRepository
{
    /// <summary>Завантажує всі стани перевірок бекапів.</summary>
    Task<IReadOnlyList<BackupCheckState>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Insert або update за ключем (Name, Kind), включно з History.</summary>
    Task UpsertAsync(BackupCheckState state, CancellationToken ct = default);

    /// <summary>Видаляє застарілі записи, ключі яких більше не входять у поточну конфігурацію BackupChecks.</summary>
    Task<int> DeleteWhereKeyNotInAsync(IReadOnlySet<string> validKeys, CancellationToken ct = default);
}
