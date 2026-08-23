using AdminConsole.Domain.Models.Reports;
using AdminConsole.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>
/// T4.12 — on-demand SLA: GET /api/sla/html returns a self-contained HTML
/// file (SlaReportHtmlRenderer) for "download report" — the same rendering
/// used by the SlaReportJob Hangfire job, just on-demand instead of
/// scheduled.
///
/// The plain-JSON GET /api/sla action (originally meant to be rendered by
/// React in the UI, per this class's earlier doc comment) was removed
/// 2026-08-23, dead-code audit — that UI was never built; the Uptime page's
/// SLA section only ever links to the HTML report. SlaReportService itself
/// is unaffected — it's also used by SlaReportJob (Hangfire) and
/// SlaReportHtmlRenderer below.
/// </summary>
public sealed class SlaController(SlaReportService slaReportService) : AdminConsoleControllerBase
{
    [HttpGet("html")]
    public ContentResult GetHtml(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] string? group = null,
        [FromQuery] string? server = null)
    {
        var report = slaReportService.Generate(new SlaReportRequest
        {
            From         = from,
            To           = to,
            GroupFilter  = group,
            ServerFilter = server
        });

        return Content(SlaReportHtmlRenderer.Render(report), "text/html");
    }
}
