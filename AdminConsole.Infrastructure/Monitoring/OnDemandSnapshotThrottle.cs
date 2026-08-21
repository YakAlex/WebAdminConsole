namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// Троттлінг + single-flight дедуплікація для "on-demand" REST-знімків
/// (PingMonitorService.PingAllNowAsync, RdpMonitorService.GetSnapshotNowAsync,
/// ZabbixPollerService.GetActiveProblemsNowAsync) — аудит-фікс (2026-08-22):
/// без цього кожен захід на відповідну сторінку (чи повторний F5, чи кілька
/// відкритих вкладок різних адмінів) незалежно запускав РЕАЛЬНУ дію проти
/// інфраструктури (ICMP ping, quser.exe на термінальний сервер, живий запит
/// у Zabbix API) — інтервал з appsettings.json (PingIntervalSeconds/
/// RdpPollIntervalSeconds/ZabbixPollIntervalSeconds) на ці REST-виклики
/// взагалі не впливав.
///
/// SemaphoreSlim(1,1) серіалізує геть усі виклики — і одночасні, і
/// послідовні: якщо другий виклик приходить, поки перший ще виконується,
/// він просто ЧЕКАЄ на завершення першого й отримує ТОЙ САМИЙ результат
/// замість запуску паралельної другої дії (single-flight, той самий
/// принцип, що golang singleflight/Go's x/sync); якщо приходить ПІСЛЯ
/// завершення, але в межах `window` — повертає закешований результат без
/// нового виклику `action` узагалі.
///
/// Кожен сервіс тримає власний екземпляр (не Singleton на весь застосунок) —
/// вікно навмисно береться з ЙОГО ЖЕ конфігурованого інтервалу опитування,
/// щоб REST-шлях поводився узгоджено з фоновим циклом, без окремого нового
/// налаштування в appsettings.json.
/// </summary>
public sealed class OnDemandSnapshotThrottle<T>(TimeSpan window)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRunAt;
    private bool _hasResult;
    private T _lastResult = default!;

    public async Task<T> GetOrRunAsync(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_hasResult && DateTimeOffset.UtcNow - _lastRunAt < window)
                return _lastResult;

            var result = await action(ct).ConfigureAwait(false);
            _lastResult = result;
            _hasResult = true;
            _lastRunAt = DateTimeOffset.UtcNow;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
