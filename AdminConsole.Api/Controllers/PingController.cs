using AdminConsole.Domain.Events;
using AdminConsole.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>
/// GET /api/ping — початковий live-знімок ping-статусів для сторінок, що
/// монтуються ПІСЛЯ старту сервісу (Overview/Ping). Раніше єдиним джерелом
/// був SignalR PingBatchResultOccurred, тож картки лишались порожніми до
/// наступного циклу опитування (до PingIntervalSeconds, за замовчуванням
/// 30с) — фронтенд не мав звідки взяти "останній відомий стан" при
/// монтуванні.
///
/// Перевикористовує PingMonitorService.PingAllNowAsync — той самий метод,
/// яким уже користується команда /ping Telegram-бота (Фаза 5): реально
/// пінгує всі сервери ЗАРАЗ і публікує PingBatchResultOccurred (інші
/// підключені клієнти теж побачать оновлення через SignalR), а не просто
/// повертає застарілий кеш зі старого циклу.
/// </summary>
public sealed class PingController(PingMonitorService pingMonitor) : AdminConsoleControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PingBatchPayload>> Get(CancellationToken ct)
    {
        var results = await pingMonitor.PingAllNowAsync(ct);
        return Ok(new PingBatchPayload(results, DateTimeOffset.Now));
    }
}
