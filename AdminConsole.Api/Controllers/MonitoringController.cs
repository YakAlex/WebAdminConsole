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
/// Step 4 (#7): background-service toggles in Settings — the same approach
/// used in WPF (RdpMonitoringEnabled/ZabbixMonitoringEnabled/BackupMonitoringEnabled
/// in UserSettings), just a REST endpoint instead of writing directly to a file.
///
/// Push after Save — MonitoringToggledOccurred (Rdp/Zabbix): ZabbixPollerService
/// and RdpMonitorService already listen for this event (Phase 4) and cancel
/// their current Task.Delay so the change takes effect immediately instead
/// of waiting for the next poll interval. BackupMonitorJob is a Hangfire
/// recurring job (not a long-lived singleton) — the toggle is read via Pull
/// at the start of each run, so no separate wake-up is needed; the next
/// scheduled run picks up the new value on its own.
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

        // Save BEFORE publishing MonitoringToggledOccurred — once woken up,
        // the pollers immediately re-read IAppSettingsRepository.GetAsync()
        // (Pull, edge case #2 from their own comments), so the new value
        // must already be in the DB before they wake up.
        //
        // Audit Zone 2 (2026-08-22): a targeted update of just the three
        // toggles (not GetAsync+SaveAsync on the full object) — so a
        // concurrent write from RdpMonitorService.UpdateRdpDailyPeakAsync
        // can't clobber it, or vice versa.
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
