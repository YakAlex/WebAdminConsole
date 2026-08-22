using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>GET /api/backups — all BackupCheckState entries (initial load for the Backups tab).</summary>
public sealed class BackupsController(IBackupStateRepository repository) : AdminConsoleControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BackupCheckState>>> Get(CancellationToken ct) =>
        Ok(await repository.LoadAllAsync(ct));
}
