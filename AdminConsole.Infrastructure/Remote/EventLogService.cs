using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using MediatR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Infrastructure.Remote;

/// <summary>
/// Reads Error/Critical records from the Windows Event Log (System +
/// Application) and publishes EventLogUpdatedOccurred.
///
/// Optimization: keeps a _lastRead timestamp between iterations.
/// First run — reads the last FetchCount errors overall.
/// Subsequent runs — scan only records newer than _lastRead, stopping as
/// soon as an old record is encountered (early exit).
/// No notification is sent if there are no new records.
///
/// T4.7: BackgroundService, tight loop (30s) — no Hangfire.
/// </summary>
public sealed class EventLogService(
    IMediator                 mediator,
    ILogger<EventLogService>  logger)
    : BackgroundService
{
    private const int FetchCount          = 20;
    private const int PollIntervalSeconds = 30;

    // Keeps the timestamp of the last record read.
    // null = first run, read the full InitialMaxScan window back.
    // non-null = incremental mode, read only what's new.
    private DateTimeOffset? _lastRead;

    // Cache of the last full snapshot — needed for React clients that
    // connect AFTER this BackgroundService has already published its first
    // notification (a SignalR event is "lost" if no one was subscribed at
    // the moment of Publish). The REST controller (Phase 6) reads this
    // field directly on initial load, without waiting for the next
    // PollIntervalSeconds.
    private readonly List<EventLogEntry> _lastSnapshot = new();
    private readonly object _snapshotLock = new();

    // Return the snapshot under lock — protects against a race with
    // FetchAndPublishAsync, which writes from the thread pool while a
    // reader (API/test) reads concurrently.
    public IReadOnlyList<EventLogEntry> LastSnapshot
    {
        get
        {
            lock (_snapshotLock)
                return _lastSnapshot.ToList();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("EventLogService started.");

        await FetchAndPublishAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(PollIntervalSeconds),
                    stoppingToken).ConfigureAwait(false);

                if (stoppingToken.IsCancellationRequested) break;

                await FetchAndPublishAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal completion on StopAsync — ignore.
        }

        logger.LogInformation("EventLogService stopped.");
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────

    private async Task FetchAndPublishAsync(CancellationToken ct)
    {
        try
        {
            // Capture the time BEFORE reading — so we don't miss records
            // that appear while we're reading.
            var readStart = DateTimeOffset.Now;
            var since     = _lastRead;

            var entries = await Task
                .Run(() => ReadErrors(since), ct)
                .ConfigureAwait(false);

            // Advance the cursor only if the read succeeded
            _lastRead = readStart;

            // Update the cache — on the first read just store it, on
            // subsequent reads prepend the new records (newest on top) and
            // trim the tail the same way the old ViewModel did.
            lock (_snapshotLock)
            {
                if (since is null)
                {
                    _lastSnapshot.Clear();
                    _lastSnapshot.AddRange(entries);
                }
                else if (entries.Count > 0)
                {
                    _lastSnapshot.InsertRange(0, entries);
                    if (_lastSnapshot.Count > FetchCount)
                        _lastSnapshot.RemoveRange(FetchCount, _lastSnapshot.Count - FetchCount);
                }
            }

            // Don't send a notification if there's nothing new.
            // Exception: the first run (since == null) — always send so
            // the UI gets the initial state.
            if (since is not null && entries.Count == 0)
            {
                logger.LogDebug("EventLogService: no new errors since {LastRead}.", since);
                return;
            }

            logger.LogDebug(
                "EventLogService: found {Count} new error(s) since {Since}.",
                entries.Count, since);

            await mediator.Publish(new EventLogUpdatedOccurred(entries), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EventLogService: failed to read event logs.");
        }
    }

    // ── Reading records ───────────────────────────────────────────────────────
    // Delegated to WinEventLogReader — shared reading logic for local
    // (this service) and remote (RemoteEventLogService).
    private static List<EventLogEntry> ReadErrors(DateTimeOffset? since)
        => WinEventLogReader.ReadErrors(".", since);
}
