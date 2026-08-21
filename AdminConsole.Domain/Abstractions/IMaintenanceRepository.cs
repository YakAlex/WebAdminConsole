using AdminConsole.Domain.Models;

namespace AdminConsole.Domain.Abstractions;

/// <summary>
/// Персистентність для MaintenanceWindow. Дзеркалить LoadFromDisk/SaveToDisk
/// з WPF MaintenanceService (Фаза 2, T2.3). Ключ запису — MaintenanceWindow.Key
/// (ServerIp або "group:{TargetGroup}") — у реляційній моделі стає реальним
/// стовпцем/первинним ключем замість [JsonIgnore]-обчислюваної властивості.
/// </summary>
public interface IMaintenanceRepository
{
    /// <summary>Завантажує всі вікна обслуговування (у т.ч. прострочені — фільтрація по часу лишається відповідальністю сервіса, як і зараз).</summary>
    Task<IReadOnlyList<MaintenanceWindow>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Insert або update за Key (StartMaintenance).</summary>
    Task UpsertAsync(MaintenanceWindow window, CancellationToken ct = default);

    /// <summary>Видаляє одне вікно за Key (EndMaintenanceEarly, автозавершення прострочених).</summary>
    Task RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>Знімає ВСІ вікна одразу (ClearAllOnShutdown при реальному виході з застосунку).</summary>
    Task RemoveAllAsync(CancellationToken ct = default);
}
