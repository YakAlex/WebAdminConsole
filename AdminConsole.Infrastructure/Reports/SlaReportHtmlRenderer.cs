using System.Globalization;
using System.Net;
using System.Text;
using AdminConsole.Domain.Models.Reports;

namespace AdminConsole.Infrastructure.Reports;

/// <summary>
/// Renders an SlaReport into a self-contained HTML file — no external
/// CSS/JS dependencies (inline &lt;style&gt;), so it opens anywhere
/// offline. A pure function (string → string), easy to test independently
/// of disk I/O.
///
/// T4.12: carried over unchanged.
/// </summary>
public static class SlaReportHtmlRenderer
{
    public static string Render(SlaReport report)
    {
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/>");
        sb.Append($"<title>SLA Report {report.From:dd.MM.yyyy}–{report.To:dd.MM.yyyy}</title>");
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
    /// Rounding must not "hide" real downtime: if downtime > 0, the text
    /// representation never shows 100.00%, even if the true value
    /// (99.998%) mathematically rounds to it.
    /// </summary>
    private static string FormatUptimePercent(double uptimePercent, TimeSpan downtime)
    {
        var rounded = Math.Round(uptimePercent, UptimePercentDecimals);

        if (downtime > TimeSpan.Zero && rounded >= 100.0)
            rounded = 100.0 - Math.Pow(10, -UptimePercentDecimals);

        return rounded.ToString($"0.{new string('0', UptimePercentDecimals)}", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The palette isn't arbitrary: it's literally the same values that
    /// <c>adminconsole-web/src/styles/tokens.scss</c> defines for the live
    /// application (audit fix item 3d — this used to be a separate, unused-
    /// anywhere-else "Material Dark" palette that had nothing in common with
    /// the real UI). No external fonts/CDN — the same fallback stack as
    /// --font-family-base, so the report stays self-contained and opens offline.
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
            <h1>SLA Report</h1>
            <div class="meta">
                Period: {report.From:dd.MM.yyyy HH:mm} – {report.To:dd.MM.yyyy HH:mm}<br/>
                Generated: {report.GeneratedAt:dd.MM.yyyy HH:mm:ss}
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
                <h2>Summary</h2>
                <div class="summary-row">
                    <div class="summary-pill"><div class="value">{overall}</div><div class="label">Overall Uptime</div></div>
                    <div class="summary-pill"><div class="value">{totalIncidents}</div><div class="label">Incidents in period</div></div>
                    <div class="summary-pill"><div class="value">{report.Servers.Count}</div><div class="label">Servers in report</div></div>
                    <div class="summary-pill"><div class="value" style="font-size:16px;">{worstLine}</div><div class="label">Worst performer</div></div>
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
                ? "<span class=\"badge badge-removed\">removed from monitoring</span>"
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
                <h2>Servers</h2>
                <table>
                    <thead><tr><th>Server</th><th>Group</th><th>Uptime</th><th>DownTime</th><th>Incidents</th><th>MTTR</th></tr></thead>
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
                ? "<span class=\"badge badge-ongoing\">ongoing</span>"
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
                <h2>Incident details</h2>
                <table>
                    <thead><tr><th>Server</th><th>Incident start</th><th>Recovered</th><th>Duration in period</th><th></th></tr></thead>
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
                    <h2>Appendix — scheduled maintenance in period</h2>
                    <table>
                        <thead><tr><th>Server</th><th>Start</th><th>End</th><th>Duration</th></tr></thead>
                        <tbody>{rows}</tbody>
                    </table>
                </div>
                """;
    }

    private static string RenderFooter(SlaReport report) => $"""
        <div class="footer">
            This report covers only the time AdminConsole was actively polling servers —
            periods when the application itself was not running are excluded from the calculation.<br/>
            AdminConsole SLA Report · generated {report.GeneratedAt:dd.MM.yyyy HH:mm:ss}
        </div>
        """;

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalHours >= 24) return $"{(int)d.TotalDays}d {d.Hours}h {d.Minutes:D2}m";
        if (d.TotalHours >= 1)  return $"{(int)d.TotalHours}h {d.Minutes:D2}m";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}m {d.Seconds:D2}s";
        return $"{d.Seconds}s";
    }

    private static string Encode(string s) => WebUtility.HtmlEncode(s);
}
