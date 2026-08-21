using AdminConsole.Domain.Models.Reports;
using AdminConsole.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>
/// T4.12 — SLA on-demand: GET /api/sla повертає JSON (React показує в UI),
/// GET /api/sla/html повертає самодостатній HTML-файл (SlaReportHtmlRenderer)
/// для "завантажити звіт" — той самий рендер, що й у Hangfire-джобі SlaReportJob,
/// лише на вимогу замість за розкладом.
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
