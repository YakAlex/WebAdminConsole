using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>
/// GET /api/downtime — усі DowntimeRecord (початкове завантаження для
/// вкладки Uptime). DELETE-ендпоінти — Крок 5 (#3): видалення закритих
/// інцидентів зі списку (аналог WPF UptimeViewModel.DeleteRecord/
/// ClearAllResolved). Обидва публікують UptimeUpdatedOccurred(повний
/// знімок) — той самий канал, яким UptimeTrackerService і так штовхає
/// живі оновлення, тож ініціатор запиту побачить зміну через SignalR
/// без окремого рефетчу.
/// </summary>
public sealed class DowntimeController(IDowntimeRepository repository, IMediator mediator) : AdminConsoleControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DowntimeRecord>>> Get(CancellationToken ct) =>
        Ok(await repository.LoadAllAsync(ct));

    /// <summary>Видаляє один запис за природним ключем (ServerIp, FellAt) — лише закриті інциденти видаляються з UI.</summary>
    [HttpDelete]
    public async Task<IActionResult> Delete(
        [FromQuery] string serverIp, [FromQuery] DateTimeOffset fellAt, CancellationToken ct)
    {
        var all = await repository.LoadAllAsync(ct);
        var target = all.FirstOrDefault(r => r.ServerIp == serverIp && r.FellAt == fellAt);
        if (target is null) return NotFound();
        if (!target.IsResolved) return BadRequest(new { error = "Не можна видалити відкритий інцидент." });

        await repository.DeleteAsync(serverIp, fellAt, ct);
        await mediator.Publish(new UptimeUpdatedOccurred(await repository.LoadAllAsync(ct)), ct);
        return NoContent();
    }

    /// <summary>Масове видалення всіх закритих інцидентів (WPF "Clear History").</summary>
    [HttpDelete("resolved")]
    public async Task<ActionResult<int>> DeleteAllResolved(CancellationToken ct)
    {
        int removed = await repository.DeleteAllResolvedAsync(ct);
        await mediator.Publish(new UptimeUpdatedOccurred(await repository.LoadAllAsync(ct)), ct);
        return Ok(removed);
    }
}
