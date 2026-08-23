namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// Throttling + single-flight deduplication for "on-demand" REST snapshots
/// (PingMonitorService.PingAllNowAsync, RdpMonitorService.GetSnapshotNowAsync,
/// ZabbixPollerService.GetActiveProblemsNowAsync) — audit fix (2026-08-22):
/// without this, every visit to the corresponding page (whether an F5 or
/// several admins with different tabs open) independently triggered a REAL
/// action against the infrastructure (ICMP ping, quser.exe against a
/// terminal server, a live call to the Zabbix API) — the appsettings.json
/// interval (PingIntervalSeconds/RdpPollIntervalSeconds/
/// ZabbixPollIntervalSeconds) had no effect at all on these REST calls.
///
/// SemaphoreSlim(1,1) serializes every call — both concurrent and
/// sequential: if a second call arrives while the first is still running,
/// it simply WAITS for the first to finish and gets the SAME result
/// instead of triggering a second parallel action (single-flight, the same
/// principle as Go's x/sync singleflight); if it arrives AFTER completion
/// but within `window` — it returns the cached result without calling
/// `action` again at all.
///
/// Each service holds its own instance (not an app-wide Singleton) — the
/// window is deliberately taken from THAT service's own configured poll
/// interval, so the REST path behaves consistently with the background
/// loop, without a separate new appsettings.json setting.
/// </summary>
public sealed class OnDemandSnapshotThrottle<T>(TimeSpan window)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRunAt;
    private bool _hasResult;
    private T _lastResult = default!;

    public async Task<T> GetOrRunAsync(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_hasResult && DateTimeOffset.UtcNow - _lastRunAt < window)
                return _lastResult;

            var result = await action(ct).ConfigureAwait(false);
            _lastResult = result;
            _hasResult = true;
            _lastRunAt = DateTimeOffset.UtcNow;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Hands the cache a result fetched OUTSIDE GetOrRunAsync — e.g. a
    /// background poll loop that ran early because of a wake-up event (a
    /// Settings change), not through the on-demand REST path. Without this,
    /// the background loop and the on-demand REST snapshot are two
    /// independent timers: the loop can fetch fresh data seconds after a
    /// Settings change while a page load/F5 still serves a pre-change
    /// snapshot from this cache for up to the rest of `window`, because
    /// nothing ever told THIS cache that anything happened.
    /// </summary>
    public async Task SetResultAsync(T result, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _lastResult = result;
            _hasResult = true;
            _lastRunAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}
