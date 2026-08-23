using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models.Reports;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Infrastructure.Reports;

/// <summary>
/// T4.12: Hangfire recurring job — a "scheduled" SLA report, complementing the
/// on-demand SlaController (REST). Computes a rolling weekly report and
/// publishes a short summary via AppLogEntryOccurred (visible in the Logs feed).
///
/// Deliberately does NOT send email/Telegram — the delivery channel for
/// scheduled reports isn't defined by either of the two plans for Phase 4
/// (the Telegram bot is Phase 5, T5.3); extending this now would mean
/// inventing unspecified functionality. The HTML render for "download the
/// report" remains exclusively on the REST endpoint (on-demand, SlaController).
/// </summary>
public sealed class SlaReportJob(
    IMediator             mediator,
    SlaReportService      slaReportService,
    ILogger<SlaReportJob> logger)
{
    private const string LogSource = "SlaReport";

    /// <summary>Audit Zone 1, Finding #7 (2026-08-22): guards against concurrent runs, same as BackupMonitorJob.</summary>
    // Bug fix (2026-08-23, audit Finding 2.1): mirrors the exact pattern
    // already applied to BackupMonitorJob.RunAsync — log a visible
    // AppLogEntryOccurred.Error before rethrowing, so a failure here shows
    // up in the in-app Logs UI, not just as an invisible Hangfire "Failed"
    // entry. Rethrowing (not swallowing) is correct here specifically
    // because this is a Hangfire job, not an infinite MediatR loop — its
    // own retry mechanism is the right recovery path.
    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    public async Task RunWeeklyAsync(CancellationToken ct = default)
    {
        try
        {
            var to   = DateTimeOffset.Now;
            var from = to.AddDays(-7);

            var report = slaReportService.Generate(new SlaReportRequest { From = from, To = to });

            logger.LogInformation(
                "SlaReportJob: weekly report {From}–{To}, {Servers} servers, overall={Overall}%.",
                from, to, report.Servers.Count, report.OverallUptimePercent);

            var overallText = report.OverallUptimePercent is { } p
                ? $"{p:0.00}%"
                : "n/a (no servers in the report)";

            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                $"Weekly SLA report ({from:dd.MM}–{to:dd.MM}): overall uptime {overallText}, " +
                $"{report.Servers.Sum(s => s.IncidentCount)} incident(s) across {report.Servers.Count} server(s)."), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "SlaReportJob: cycle failed.");
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Weekly SLA report failed — {ex.GetType().Name}: {ex.Message}."), CancellationToken.None);
            throw;
        }
    }
}
