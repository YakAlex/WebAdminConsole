using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>GET /api/backups — усі BackupCheckState (початкове завантаження для вкладки Backups).</summary>
public sealed class BackupsController(IBackupStateRepository repository) : AdminConsoleControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BackupCheckState>>> Get(CancellationToken ct) =>
        Ok(await repository.LoadAllAsync(ct));
}
