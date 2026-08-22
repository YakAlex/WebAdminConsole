using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

public sealed record MonitoringTogglesResponse(
    bool RdpMonitoringEnabled, bool ZabbixMonitoringEnabled, bool BackupMonitoringEnabled);

public sealed record UpdateMonitoringTogglesRequest(
    bool RdpMonitoringEnabled, bool ZabbixMonitoringEnabled, bool BackupMonitoringEnabled);

/// <summary>
/// Крок 4 (#7): вмикачі фонових сервісів у Settings — той самий підхід, що
/// був у WPF (RdpMonitoringEnabled/ZabbixMonitoringEnabled/BackupMonitoringEnabled
/// у UserSettings), лише REST-ендпоінт замість прямого запису в файл.
///
/// Пуш після Save — MonitoringToggledOccurred (Rdp/Zabbix): ZabbixPollerService
/// і RdpMonitorService вже слухають цю подію (Фаза 4) і скасовують поточний
/// Task.Delay, щоб зміна подіяла негайно, а не чекала наступного інтервалу
/// опитування. BackupMonitorJob — Hangfire recurring job (не довгоживучий
/// singleton), toggle читається Pull-ом на початку кожного запуску — окремий
/// wake-up не потрібен, наступний запланований запуск і так підхопить нове
/// значення.
/// </summary>
public sealed class MonitoringController(IAppSettingsRepository repository, IMediator mediator) : AdminConsoleControllerBase
{
    [HttpGet("toggles")]
    public async Task<ActionResult<MonitoringTogglesResponse>> GetToggles(CancellationToken ct)
    {
        var settings = await repository.GetAsync(ct);
        return Ok(new MonitoringTogglesResponse(
            settings.RdpMonitoringEnabled, settings.ZabbixMonitoringEnabled, settings.BackupMonitoringEnabled));
    }

    [HttpPut("toggles")]
    public async Task<ActionResult<MonitoringTogglesResponse>> UpdateToggles(
        [FromBody] UpdateMonitoringTogglesRequest request, CancellationToken ct)
    {
        var settings = await repository.GetAsync(ct);

        bool rdpChanged    = settings.RdpMonitoringEnabled    != request.RdpMonitoringEnabled;
        bool zabbixChanged = settings.ZabbixMonitoringEnabled != request.ZabbixMonitoringEnabled;
        bool backupChanged = settings.BackupMonitoringEnabled != request.BackupMonitoringEnabled;

        // Зберігаємо ПЕРЕД публікацією MonitoringToggledOccurred — поллери,
        // прокинувшись, одразу перечитують IAppSettingsRepository.GetAsync()
        // (Pull, edge-case #2 з їхніх власних коментарів), тож нове значення
        // мусить вже лежати в БД до того, як вони прокинуться.
        //
        // Аудит Зона 2 (2026-08-22): точкове оновлення лише трьох перемикачів
        // (не GetAsync+SaveAsync повного об'єкта) — щоб паралельний запис
        // RdpMonitorService.UpdateRdpDailyPeakAsync не міг затерти й навпаки.
        await repository.UpdateMonitoringTogglesAsync(
            request.RdpMonitoringEnabled, request.ZabbixMonitoringEnabled, request.BackupMonitoringEnabled, ct);

        if (rdpChanged)
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Rdp, request.RdpMonitoringEnabled), ct);
        if (zabbixChanged)
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Zabbix, request.ZabbixMonitoringEnabled), ct);
        if (backupChanged)
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Backups, request.BackupMonitoringEnabled), ct);

        return Ok(new MonitoringTogglesResponse(
            request.RdpMonitoringEnabled, request.ZabbixMonitoringEnabled, request.BackupMonitoringEnabled));
    }
}
