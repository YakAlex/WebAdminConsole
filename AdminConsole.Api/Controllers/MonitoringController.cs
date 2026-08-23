using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

public sealed record MonitoringTogglesResponse(
    bool RdpMonitoringEnabled, bool ZabbixMonitoringEnabled, bool BackupMonitoringEnabled, int ZabbixMinSeverity);

public sealed record UpdateMonitoringTogglesRequest(
    bool RdpMonitoringEnabled, bool ZabbixMonitoringEnabled, bool BackupMonitoringEnabled, int ZabbixMinSeverity);

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
///
/// A ZabbixMinSeverity change also publishes MonitoringToggledOccurred(Zabbix)
/// — same wake-up, not just the two boolean toggles (bug fix, 2026-08-23).
/// </summary>
public sealed class MonitoringController(IAppSettingsRepository repository, IMediator mediator) : AdminConsoleControllerBase
{
    [HttpGet("toggles")]
    public async Task<ActionResult<MonitoringTogglesResponse>> GetToggles(CancellationToken ct)
    {
        var settings = await repository.GetAsync(ct);
        return Ok(new MonitoringTogglesResponse(
            settings.RdpMonitoringEnabled, settings.ZabbixMonitoringEnabled, settings.BackupMonitoringEnabled,
            settings.ZabbixMinSeverity));
    }

    [HttpPut("toggles")]
    public async Task<ActionResult<MonitoringTogglesResponse>> UpdateToggles(
        [FromBody] UpdateMonitoringTogglesRequest request, CancellationToken ct)
    {
        // Code-review finding (2026-08-23, Critical): an old cached frontend
        // bundle sends a PUT body without zabbixMinSeverity (pre-dating this
        // field) — System.Text.Json record binding silently defaults a
        // missing int to 0, which is out of the range the Settings slider
        // (and SEVERITY_LEVELS on the frontend) actually supports. Without
        // this guard, 0 gets persisted and crashes the Settings page (and,
        // via the single app-wide ErrorBoundary, the whole SPA) for every
        // user on next load.
        if (request.ZabbixMinSeverity is < 1 or > 5)
            return BadRequest(new { error = "ZabbixMinSeverity must be between 1 and 5." });

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

        // ZabbixMinSeverity has its own targeted update method for the same
        // no-clobbering reason.
        bool severityChanged = settings.ZabbixMinSeverity != request.ZabbixMinSeverity;
        if (severityChanged)
            await repository.UpdateZabbixMinSeverityAsync(request.ZabbixMinSeverity, ct);

        if (rdpChanged)
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Rdp, request.RdpMonitoringEnabled), ct);

        // Bug fix (2026-08-23): a severity-only change (zabbixChanged == false)
        // used to publish nothing, so ZabbixPollerService kept polling with the
        // stale severity list for up to ZabbixPollIntervalSeconds (180s in
        // production) — indistinguishable from "the setting doesn't do
        // anything" to whoever just saved it. Reusing MonitoringToggledOccurred
        // (rather than a new event type) because ZabbixPollerService.Handle
        // already wakes up on ANY Zabbix-targeted toggle event, regardless of
        // the carried `enabled` value — publishing the current (unchanged)
        // enabled state here is a no-op for that value but still triggers the
        // wake-up.
        if (zabbixChanged || severityChanged)
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Zabbix, request.ZabbixMonitoringEnabled), ct);
        if (backupChanged)
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Backups, request.BackupMonitoringEnabled), ct);

        return Ok(new MonitoringTogglesResponse(
            request.RdpMonitoringEnabled, request.ZabbixMonitoringEnabled, request.BackupMonitoringEnabled,
            request.ZabbixMinSeverity));
    }
}
