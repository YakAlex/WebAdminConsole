using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models.Reports;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Infrastructure.Reports;

/// <summary>
/// T4.12: Hangfire recurring job — SLA-звіт "за розкладом", доповнення до
/// on-demand SlaController (REST). Рахує ковзний тижневий звіт і публікує
/// коротке зведення через AppLogEntryOccurred (видно у Logs feed).
///
/// Свідомо НЕ надсилає email/Telegram — канал доставки розкладних звітів
/// не визначений жодним з двох планів для Фази 4 (Telegram-бот — Фаза 5,
/// T5.3); розширювати це зараз означало б вигадувати недомовлений
/// функціонал. HTML-рендер для "завантажити звіт" лишається виключно
/// на REST-ендпоінті (on-demand, SlaController).
/// </summary>
public sealed class SlaReportJob(
    IMediator             mediator,
    SlaReportService      slaReportService,
    ILogger<SlaReportJob> logger)
{
    private const string LogSource = "SlaReport";

    /// <summary>Аудит Зона 1, Знахідка №7 (2026-08-22): захист від паралельного запуску, як BackupMonitorJob.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    public async Task RunWeeklyAsync(CancellationToken ct = default)
    {
        var to   = DateTimeOffset.Now;
        var from = to.AddDays(-7);

        var report = slaReportService.Generate(new SlaReportRequest { From = from, To = to });

        logger.LogInformation(
            "SlaReportJob: тижневий звіт {From}–{To}, {Servers} серверів, overall={Overall}%.",
            from, to, report.Servers.Count, report.OverallUptimePercent);

        var overallText = report.OverallUptimePercent is { } p
            ? $"{p:0.00}%"
            : "н/д (немає серверів у звіті)";

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Тижневий SLA-звіт ({from:dd.MM}–{to:dd.MM}): overall uptime {overallText}, " +
            $"{report.Servers.Sum(s => s.IncidentCount)} інцидент(ів) на {report.Servers.Count} сервер(ах)."), ct);
    }
}
