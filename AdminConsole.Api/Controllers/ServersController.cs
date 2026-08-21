using AdminConsole.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AdminConsole.Api.Controllers;

/// <summary>GET /api/servers — сконфігурований список серверів (Servers у appsettings.json).</summary>
public sealed class ServersController(IOptions<List<ServerEntry>> servers) : AdminConsoleControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<ServerEntry>> Get() => Ok(servers.Value);
}
