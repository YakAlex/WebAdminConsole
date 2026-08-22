using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>Default "Viewer" authorization policy applied to the whole API (T3.9/T3.11).</summary>
[ApiController]
[Authorize(Policy = "Viewer")]
[Route("api/[controller]")]
public abstract class AdminConsoleControllerBase : ControllerBase;
