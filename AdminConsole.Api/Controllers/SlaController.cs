using AdminConsole.Domain.Models.Reports;
using AdminConsole.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>
/// T4.12 — on-demand SLA: GET /api/sla returns JSON (rendered by React in the
/// UI), GET /api/sla/html returns a self-contained HTML file
/// (SlaReportHtmlRenderer) for "download report" — the same rendering used
/// by the SlaReportJob Hangfire job, just on-demand instead of scheduled.
/// </summary>
public sealed class SlaController(SlaReportService slaReportService) : AdminConsoleControllerBase
{
    [HttpGet]
    public ActionResult<SlaReport> Get(
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

        return Ok(report);
    }

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
