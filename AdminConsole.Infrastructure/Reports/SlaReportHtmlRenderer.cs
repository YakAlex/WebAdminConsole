using System.Globalization;
using System.Net;
using System.Text;
using AdminConsole.Domain.Models.Reports;

namespace AdminConsole.Infrastructure.Reports;

/// <summary>
/// Рендерить SlaReport у самодостатній HTML-файл — жодних зовнішніх
/// CSS/JS-залежностей (inline &lt;style&gt;), тому відкривається будь-де
/// офлайн. Чиста функція (string → string), легко тестується окремо
/// від диска.
///
/// T4.12: перенесено без змін.
/// </summary>
public static class SlaReportHtmlRenderer
{
    public static string Render(SlaReport report)
    {
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html><html lang=\"uk\"><head><meta charset=\"utf-8\"/>");
        sb.Append($"<title>SLA-звіт {report.From:dd.MM.yyyy}–{report.To:dd.MM.yyyy}</title>");
        sb.Append(Styles());
        sb.Append("</head><body>");

        sb.Append(RenderHeader(report));
        sb.Append(RenderSummary(report));
        sb.Append(RenderServersTable(report));
        sb.Append(RenderIncidentDetails(report));
        sb.Append(RenderMaintenanceAppendix(report));
        sb.Append(RenderFooter(report));

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private const int UptimePercentDecimals = 2;

    /// <summary>
    /// Округлення не повинно "ховати" реальний даунтайм: якщо downtime > 0,
    /// текстове представлення ніколи не показує 100.00%, навіть якщо справжнє
    /// значення (99.998%) до нього математично округлюється.
    /// </summary>
    private static string FormatUptimePercent(double uptimePercent, TimeSpan downtime)
    {
        var rounded = Math.Round(uptimePercent, UptimePercentDecimals);

        if (downtime > TimeSpan.Zero && rounded >= 100.0)
            rounded = 100.0 - Math.Pow(10, -UptimePercentDecimals);

        return rounded.ToString($"0.{new string('0', UptimePercentDecimals)}", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Палітра — не довільна: буквально ті самі значення, що
    /// <c>adminconsole-web/src/styles/tokens.scss</c> визначає для живого
    /// застосунку (аудит-фікс п.3d — раніше тут була окрема, ніде більше не
    /// вжита "Material Dark" палітра, яка не мала нічого спільного з
    /// реальним UI). Жодних зовнішніх шрифтів/CDN — той самий fallback-стек,
    /// що й --font-family-base, аби звіт лишався самодостатнім і відкривався офлайн.
    /// </summary>
    private static string Styles() => """
        <style>
            :root { color-scheme: dark; }
            body {
                background: #080d12; color: #f1f5f7;
                font-family: 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif;
                margin: 0; padding: 32px;
            }
            .card {
                background: linear-gradient(180deg, #111c25, #101923);
                border: 1px solid #1e303a;
                border-radius: 12px; padding: 20px 24px; margin-bottom: 20px;
            }
            h1 { font-size: 23px; font-weight: 600; margin: 0 0 4px 0; color: #f1f5f7; }
            h2 { font-size: 12px; color: #36c8d5; margin: 0 0 12px 0; font-weight: 600;
                 text-transform: uppercase; letter-spacing: .06em; }
            .meta { color: #8799a4; font-size: 13px; }
            .summary-row { display: flex; gap: 12px; flex-wrap: wrap; }
            .summary-pill { background: #14202a; border: 1px solid #1e303a; border-radius: 8px; padding: 14px 20px; min-width: 140px; }
            .summary-pill .value { font-size: 24px; font-weight: 600; color: #f1f5f7; }
            .summary-pill .label { font-size: 11px; color: #8799a4; text-transform: uppercase; letter-spacing: .04em; }
            table { width: 100%; border-collapse: collapse; font-size: 13px; }
            th { text-align: left; color: #8799a4; font-size: 11px; font-weight: 600; text-transform: uppercase;
                 letter-spacing: .03em; padding: 8px 12px; border-bottom: 1px solid #1e303a; }
            td { padding: 10px 12px; border-bottom: 1px solid #1e303a; color: #f1f5f7; }
            tr:last-child td { border-bottom: none; }
            .badge { display: inline-block; padding: 2px 8px; border-radius: 999px; font-size: 11px; font-weight: 600; }
            .badge-removed { background: rgba(243,200,75,.12); color: #f3c84b; margin-left: 8px; }
            .badge-ongoing { background: rgba(255,92,97,.12); color: #ff5c61; }
            .badge-maintenance { background: rgba(54,200,213,.1); color: #36c8d5; }
            .uptime-good { color: #35d36a; font-weight: 600; }
            .uptime-warn { color: #f3c84b; font-weight: 600; }
            .uptime-bad  { color: #ff5c61; font-weight: 600; }
            .mono { font-family: Consolas, "Courier New", monospace; color: #8799a4; }
            .footer { color: #53656f; font-size: 11px; margin-top: 24px; line-height: 1.6; }
        </style>
        """;

    private static string RenderHeader(SlaReport report) => $"""
        <div class="card">
            <h1>SLA-звіт</h1>
            <div class="meta">
                Період: {report.From:dd.MM.yyyy HH:mm} – {report.To:dd.MM.yyyy HH:mm}<br/>
                Згенеровано: {report.GeneratedAt:dd.MM.yyyy HH:mm:ss}
            </div>
        </div>
        """;

    private static string RenderSummary(SlaReport report)
    {
        var totalDowntime = TimeSpan.FromTicks(report.Servers.Sum(s => s.DowntimeInPeriod.Ticks));

        var overall = report.OverallUptimePercent is { } p
            ? $"{FormatUptimePercent(p, totalDowntime)}%"
            : "—";

        var totalIncidents = report.Servers.Sum(s => s.IncidentCount);
        var worst = report.Servers.FirstOrDefault();
        var worstLine = worst is not null
            ? $"{Encode(worst.ServerName)} ({FormatUptimePercent(worst.UptimePercent, worst.DowntimeInPeriod)}%)"
            : "—";

        return $"""
            <div class="card">
                <h2>Зведення</h2>
                <div class="summary-row">
                    <div class="summary-pill"><div class="value">{overall}</div><div class="label">Загальний Uptime</div></div>
                    <div class="summary-pill"><div class="value">{totalIncidents}</div><div class="label">Інцидентів за період</div></div>
                    <div class="summary-pill"><div class="value">{report.Servers.Count}</div><div class="label">Серверів у звіті</div></div>
                    <div class="summary-pill"><div class="value" style="font-size:16px;">{worstLine}</div><div class="label">Найгірший показник</div></div>
                </div>
            </div>
            """;
    }

    private static string RenderServersTable(SlaReport report)
    {
        var rows = new StringBuilder();
        foreach (var s in report.Servers)
        {
            var uptimeClass = s.UptimePercent switch
            {
                >= 99.9 => "uptime-good",
                >= 99.0 => "uptime-warn",
                _       => "uptime-bad"
            };

            var removedBadge = s.IsRemovedFromMonitoring
                ? "<span class=\"badge badge-removed\">видалено з моніторингу</span>"
                : "";

            rows.Append($"""
                         <tr>
                             <td>{Encode(s.ServerName)}{removedBadge}</td>
                             <td>{Encode(s.ServerGroup)}</td>
                             <td class="{uptimeClass}">{FormatUptimePercent(s.UptimePercent, s.DowntimeInPeriod)}%</td>
                             <td class="mono">{FormatDuration(s.DowntimeInPeriod)}</td>
                             <td>{s.IncidentCount}</td>
                             <td class="mono">{(s.Mttr is { } m ? FormatDuration(m) : "—")}</td>
                         </tr>
                         """);
        }

        return $"""
            <div class="card">
                <h2>Сервери</h2>
                <table>
                    <thead><tr><th>Сервер</th><th>Група</th><th>Uptime</th><th>DownTime</th><th>Інцидентів</th><th>MTTR</th></tr></thead>
                    <tbody>{rows}</tbody>
                </table>
            </div>
            """;
    }

    private static string RenderIncidentDetails(SlaReport report)
    {
        var rows = new StringBuilder();
        foreach (var s in report.Servers)
        foreach (var i in s.Incidents)
        {
            var recoveredCell = i.IsOngoing
                ? "<span class=\"badge badge-ongoing\">триває</span>"
                : $"<span class=\"mono\">{i.RecoveredAt:dd.MM HH:mm:ss}</span>";

            var maintenanceBadge = i.ClosedByMaintenance
                ? "<span class=\"badge badge-maintenance\">maintenance</span>"
                : "";

            rows.Append($"""
                <tr>
                    <td>{Encode(s.ServerName)}</td>
                    <td class="mono">{i.FellAt:dd.MM HH:mm:ss}</td>
                    <td>{recoveredCell}</td>
                    <td class="mono">{FormatDuration(i.EffectiveDuration)}</td>
                    <td>{maintenanceBadge}</td>
                </tr>
                """);
        }

        if (rows.Length == 0) return "";

        return $"""
            <div class="card">
                <h2>Деталі інцидентів</h2>
                <table>
                    <thead><tr><th>Сервер</th><th>Початок інциденту</th><th>Відновлено</th><th>Тривалість у періоді</th><th></th></tr></thead>
                    <tbody>{rows}</tbody>
                </table>
            </div>
            """;
    }

    private static string RenderMaintenanceAppendix(SlaReport report)
    {
        if (report.MaintenanceAppendix.Count == 0) return "";

        var rows = new StringBuilder();
        foreach (var i in report.MaintenanceAppendix)
        {
            rows.Append($"""
                         <tr>
                             <td>{Encode(i.ServerName)}</td>
                             <td class="mono">{i.FellAt:dd.MM HH:mm:ss}</td>
                             <td class="mono">{(i.RecoveredAt is { } r ? r.ToString("dd.MM HH:mm:ss") : "—")}</td>
                             <td class="mono">{FormatDuration(i.EffectiveDuration)}</td>
                         </tr>
                         """);
        }

        return $"""
                <div class="card">
                    <h2>Додаток — планове обслуговування за період</h2>
                    <table>
                        <thead><tr><th>Сервер</th><th>Початок</th><th>Кінець</th><th>Тривалість</th></tr></thead>
                        <tbody>{rows}</tbody>
                    </table>
                </div>
                """;
    }

    private static string RenderFooter(SlaReport report) => $"""
        <div class="footer">
            Звіт покриває лише час, коли AdminConsole активно опитував сервери —
            періоди, коли сам застосунок не працював, до розрахунку не входять.<br/>
            AdminConsole SLA Report · згенеровано {report.GeneratedAt:dd.MM.yyyy HH:mm:ss}
        </div>
        """;

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalHours >= 24) return $"{(int)d.TotalDays}д {d.Hours}г {d.Minutes:D2}хв";
        if (d.TotalHours >= 1)  return $"{(int)d.TotalHours}г {d.Minutes:D2}хв";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}хв {d.Seconds:D2}с";
        return $"{d.Seconds}с";
    }

    private static string Encode(string s) => WebUtility.HtmlEncode(s);
}
