using System.Collections.Concurrent;
using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// Manages planned Maintenance Windows.
///
/// Hybrid Pull + Push model:
///   - Pull: pollers (Ping, RDP) synchronously call IsUnderMaintenance
///     before sending a Warning/Error notification — no MediatR delay.
///   - Push: StartMaintenance / auto-completion in ExecuteAsync send
///     MaintenanceChangedOccurred — UptimeTracker closes incidents,
///     PingMonitor regenerates alerts, SignalR updates the UI instantly.
///
/// Storage — ConcurrentDictionary, because it's read concurrently from
/// several background threads (Ping/RDP pollers on every cycle) and
/// written from the future Settings API (UI) and from its own background
/// auto-completion loop.
///
/// T4.1: migrated FIRST among the stateful services — PingMonitorService
/// and UptimeTrackerService depend on it. Replacing LoadFromDisk() in the
/// constructor (a synchronous call, no longer possible with an async
/// repository) with an await in StartAsync, which the Generic Host is
/// guaranteed to await BEFORE the next registered IHostedService starts.
///
/// IServiceScopeFactory instead of injecting IMaintenanceRepository
/// directly: the repository is Scoped (bound to the Scoped
/// AdminConsoleDbContext), while this service is Singleton. DI forbids a
/// Singleton from holding a Scoped dependency directly (only through a
/// scope factory) — the standard, documented Microsoft pattern for a
/// BackgroundService that needs EF Core.
/// </summary>
public sealed class MaintenanceService(
    IMediator                    mediator,
    IServiceScopeFactory         scopeFactory,
    ILogger<MaintenanceService>  logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<string, MaintenanceWindow> _windows = new();

    private const string LogSource            = "Maintenance";
    private const int    CheckIntervalSeconds = 30;

    private async Task<T> WithRepositoryAsync<T>(Func<IMaintenanceRepository, Task<T>> action)
    {
        using var scope = scopeFactory.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IMaintenanceRepository>());
    }

    private Task WithRepositoryAsync(Func<IMaintenanceRepository, Task> action) =>
        WithRepositoryAsync(async r => { await action(r); return true; });

    // ── Lifecycle: guaranteed load BEFORE any other service starts ──

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadFromDbAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    // ── Pull API — called from background pollers ─────────────────────────

    public bool IsUnderMaintenance(string serverIp, string group)
    {
        var now = DateTimeOffset.Now;

        if (_windows.TryGetValue(serverIp, out var w) && w.IsActiveAt(now))
            return true;

        if (!string.IsNullOrEmpty(group) &&
            _windows.TryGetValue($"group:{group}", out var gw) && gw.IsActiveAt(now))
            return true;

        return false;
    }

    /// <summary>Returns the active window (for the UI — to show Reason/To in a tooltip).</summary>
    public MaintenanceWindow? GetActiveWindow(string serverIp, string group)
    {
        var now = DateTimeOffset.Now;

        if (_windows.TryGetValue(serverIp, out var w) && w.IsActiveAt(now))
            return w;

        if (!string.IsNullOrEmpty(group) &&
            _windows.TryGetValue($"group:{group}", out var gw) && gw.IsActiveAt(now))
            return gw;

        return null;
    }

    /// <summary>
    /// All active maintenance windows right now. A public read-only
    /// snapshot — needed by TelegramBotService (Phase 5) for the
    /// "Maintenance" command/button, without needing to separately cache
    /// state via MediatR.
    /// </summary>
    public IReadOnlyList<MaintenanceWindow> GetActiveWindows()
    {
        var now = DateTimeOffset.Now;
        return _windows.Values.Where(w => w.IsActiveAt(now)).ToList();
    }

    // ── Push API — called from the future Settings/Maintenance API ─────

    public async Task StartMaintenanceAsync(MaintenanceWindow window, CancellationToken ct = default)
    {
        _windows[window.Key] = window;
        await WithRepositoryAsync(r => r.UpsertAsync(window, ct));

        logger.LogInformation(
            "Maintenance started: {Key}, until {To}",
            window.Key, window.To?.ToString() ?? "no limit");

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Maintenance started for {window.DisplayName}: " +
            $"{(string.IsNullOrWhiteSpace(window.Reason) ? "no reason given" : window.Reason)} " +
            $"({(window.To is { } to ? $"until {to.ToLocalTime():dd.MM HH:mm}" : "no time limit")})."), ct);

        await mediator.Publish(new MaintenanceChangedOccurred(MaintenanceAction.Started, window), ct);
    }

    /// <summary>
    /// Early manual completion (an admin restored the server ahead of
    /// schedule). Returns false if no window with this Key exists anymore
    /// (idempotent no-op) — the REST controller (audit fix #1) uses this to
    /// distinguish 404 vs 204.
    /// </summary>
    public async Task<bool> EndMaintenanceEarlyAsync(string key, CancellationToken ct = default)
    {
        if (!_windows.TryRemove(key, out var window))
            return false;

        await WithRepositoryAsync(r => r.RemoveAsync(key, ct));

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Maintenance for {window.DisplayName} ended manually."), ct);

        await mediator.Publish(new MaintenanceChangedOccurred(MaintenanceAction.Ended, window), ct);
        return true;
    }

    /// <summary>
    /// Clears ALL active maintenance windows (including "no time limit"
    /// ones) and removes them from the DB. Called on graceful host
    /// shutdown — Maintenance Mode is intentionally not meant to survive
    /// an app restart: otherwise a forgotten "no limit" window could
    /// silently suppress alerts for weeks after the service was simply
    /// restarted.
    /// </summary>
    public async Task ClearAllOnShutdownAsync(CancellationToken ct = default)
    {
        var windows = _windows.Values.ToList();
        if (windows.Count == 0) return;

        _windows.Clear();
        await WithRepositoryAsync(r => r.RemoveAllAsync(ct));

        foreach (var window in windows)
            await mediator.Publish(new MaintenanceChangedOccurred(MaintenanceAction.Ended, window), ct);

        logger.LogInformation(
            "MaintenanceService: {Count} maintenance window(s) cleared on app shutdown.",
            windows.Count);
    }

    // ── Background auto-completion of expired windows ────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MaintenanceService started. {Count} active window(s) loaded.",
            _windows.Count);

        // Audit Zone 1 (2026-08-22): previously the loop body itself
        // (reading/removing expired windows, publishing events) had NO
        // try/catch at all — only the Task.Delay was protected. Any
        // transient DB exception while completing a maintenance window
        // used to bubble up unhandled and take down the whole host
        // (BackgroundServiceExceptionBehavior). A cycle that throws is now
        // simply skipped — the next cycle (after CheckIntervalSeconds)
        // will try again.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(CheckIntervalSeconds), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                var now = DateTimeOffset.Now;
                var expired = _windows.Where(kv => kv.Value.To is not null && kv.Value.To < now).ToList();
                if (expired.Count == 0) continue;

                foreach (var (key, window) in expired)
                {
                    if (!_windows.TryRemove(key, out _)) continue;

                    await WithRepositoryAsync(r => r.RemoveAsync(key, stoppingToken));

                    await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                        $"Maintenance for {window.DisplayName} ended (time expired)."), stoppingToken);

                    await mediator.Publish(new MaintenanceChangedOccurred(MaintenanceAction.Ended, window), stoppingToken);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "MaintenanceService: error in the window-completion loop — skipping this cycle.");
            }
        }
    }

    // ── Startup load ─────────────────────────────────────────────────────────

    private async Task LoadFromDbAsync(CancellationToken ct)
    {
        try
        {
            var windows = await WithRepositoryAsync(r => r.LoadAllAsync(ct));
            var now = DateTimeOffset.Now;

            foreach (var w in windows)
                if (w.To is null || w.To >= now)
                    _windows[w.Key] = w;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MaintenanceService: error loading from DB.");
        }
    }
}
