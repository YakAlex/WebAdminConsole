using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// Bug fix (2026-08-23, audit Finding 6.1): AppLogEntries had no
/// retention/pruning policy anywhere — every background service (Ping,
/// RDP, Zabbix, Backup, Uptime) writes to it every cycle, forever. Daily
/// Hangfire recurring job, same shape as BackupMonitorJob/SlaReportJob.
/// 90 days is a reasonable default for infrastructure/operational logs —
/// long enough to investigate an incident from a few weeks back, short
/// enough to keep the table (and the SQLite file) bounded.
/// </summary>
public sealed class AppLogRetentionJob(
    IAppLogRepository            repository,
    ILogger<AppLogRetentionJob>  logger,
    IMediator                    mediator)
{
    private const string LogSource     = "AppLogRetention";
    private const int    RetentionDays = 90;

    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            var cutoff = DateTimeOffset.Now.AddDays(-RetentionDays);
            int removed = await repository.DeleteOlderThanAsync(cutoff, ct);

            if (removed > 0)
            {
                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    $"Removed {removed} log entr{(removed == 1 ? "y" : "ies")} older than {RetentionDays} days."), ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "AppLogRetentionJob: cycle failed.");
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Log retention cleanup failed — {ex.GetType().Name}: {ex.Message}."), CancellationToken.None);
            throw;
        }
    }
}
