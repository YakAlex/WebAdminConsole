using AdminConsole.Domain.Events;
using AdminConsole.Infrastructure.Configuration;
using AdminConsole.Infrastructure.Security;
using AdminConsole.Infrastructure.Zabbix;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AdminConsole.Api.Controllers;

public sealed record CredentialsStatusResponse(
    ZabbixCredentialsStatus   Zabbix,
    TelegramCredentialsStatus Telegram);

public sealed record ZabbixCredentialsStatus(
    bool HasCredentials, bool UsesApiToken, string MaskedSecret, string Username);

public sealed record TelegramCredentialsStatus(bool HasCredentials, string MaskedToken);

public sealed record StoreZabbixTokenRequest(string Token);
public sealed record StoreZabbixCredentialsRequest(string Username, string Password);
public sealed record StoreTelegramTokenRequest(string BotToken);

/// <summary>
/// Результат негайної перевірки з'єднання одразу після Save (#5 UX-беклогу:
/// раніше юзер зберігав токен і не мав жодного фідбеку — доводилось чекати
/// до 180с наступного poll-циклу і сподіватись, що щось з'явиться в Logs).
/// </summary>
public sealed record ZabbixTestResult(bool Success, string? Version, string? Error);

/// <summary>
/// T5.2 — GET/POST/DELETE для credentials (Zabbix/Telegram). Захищено
/// тією ж політикою "Viewer" (AdminConsole-Admins), що й решта API —
/// React-сторінка "Налаштування → Облікові дані" (Фаза 6) буде тонким
/// клієнтом над цими ендпоінтами замість WPF credential-модалок.
///
/// RDP-ендпоінтів тут більше немає: бекенд-служба працює під виділеним
/// доменним акаунтом (DOMAIN\svc_adminconsole) з правами на цільових
/// серверах, тож quser.exe відпрацьовує через Kerberos у контексті
/// самого процесу — окремих RDP credentials зберігати не потрібно.
///
/// Кожен успішний Save/Clear публікує CredentialsChangedOccurred — той самий
/// механізм, яким ZabbixPollerService прокидається з очікування (Фаза 4) і
/// негайно застосовує нові credentials без чекання на наступний інтервал
/// опитування.
/// </summary>
public sealed class CredentialsController(
    CredentialStore credentials,
    IMediator mediator,
    ZabbixApiClient zabbixClient,
    IOptions<MonitoringSettings> monitoringSettings) : AdminConsoleControllerBase
{
    private const string ZabbixLogSource = "ZabbixPoller";

    /// <summary>
    /// Негайна перевірка щойно збереженого токена/сесії через apiinfo.version +
    /// user.checkAuthentication (ZabbixApiClient.TestConnectionAsync — до цього
    /// був написаний, але ніде не викликався). Результат публікується і в
    /// AppLogEntries (видимо в Logs одразу, без очікування poll-циклу), і
    /// повертається в HTTP-відповіді (видимо в Settings одразу).
    /// </summary>
    private async Task<ZabbixTestResult> TestAndLogAsync(string tokenForBearerHeader, CancellationToken ct)
    {
        var url = monitoringSettings.Value.ZabbixUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            const string msg = "ZabbixUrl не налаштований у appsettings.json (Monitoring:ZabbixUrl) — перевірку неможливо виконати.";
            await mediator.Publish(AppLogEntryOccurred.Warning(ZabbixLogSource, $"Zabbix: {msg}"), ct);
            return new ZabbixTestResult(false, null, msg);
        }

        var (success, version, error) = await zabbixClient.TestConnectionAsync(url, tokenForBearerHeader, ct);

        await mediator.Publish(success
            ? AppLogEntryOccurred.Success(ZabbixLogSource, $"Zabbix: з'єднання перевірено успішно (версія {version}).")
            : AppLogEntryOccurred.Warning(ZabbixLogSource, $"Zabbix: перевірка з'єднання не пройшла — {error}"), ct);

        return new ZabbixTestResult(success, version, error);
    }

    [HttpGet]
    public ActionResult<CredentialsStatusResponse> Get()
    {
        return Ok(new CredentialsStatusResponse(
            new ZabbixCredentialsStatus(
                credentials.HasZabbixCredentials,
                credentials.ZabbixUsesApiToken,
                credentials.GetZabbixTokenMasked(),
                credentials.GetZabbix().Username),
            new TelegramCredentialsStatus(credentials.HasTelegramCredentials, credentials.GetTelegramTokenMasked())));
    }

    // ── Zabbix ───────────────────────────────────────────────────────────────

    [HttpPost("zabbix/token")]
    public async Task<ActionResult<ZabbixTestResult>> SaveZabbixToken([FromBody] StoreZabbixTokenRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return BadRequest(new { error = "Token обов'язковий." });

        try
        {
            await credentials.StoreZabbixTokenAsync(request.Token, ct);
        }
        catch (CredentialProtectionException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }

        await mediator.Publish(new CredentialsChangedOccurred(CredentialTarget.Zabbix, CredentialAction.Saved), ct);

        var result = await TestAndLogAsync(request.Token, ct);
        return Ok(result);
    }

    [HttpPost("zabbix/password")]
    public async Task<ActionResult<ZabbixTestResult>> SaveZabbixCredentials([FromBody] StoreZabbixCredentialsRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "Username і Password обов'язкові." });

        try
        {
            await credentials.StoreZabbixCredentialsAsync(request.Username, request.Password, ct);
        }
        catch (CredentialProtectionException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }

        await mediator.Publish(new CredentialsChangedOccurred(CredentialTarget.Zabbix, CredentialAction.Saved), ct);

        var url = monitoringSettings.Value.ZabbixUrl;
        var sessionToken = string.IsNullOrWhiteSpace(url)
            ? null
            : await zabbixClient.LoginAsync(url, request.Username, request.Password, ct);

        if (sessionToken is null)
        {
            const string msg = "Не вдалося авторизуватись у Zabbix — перевірте логін/пароль.";
            await mediator.Publish(AppLogEntryOccurred.Warning(ZabbixLogSource, $"Zabbix: {msg}"), ct);
            return Ok(new ZabbixTestResult(false, null, msg));
        }

        var result = await TestAndLogAsync(sessionToken, ct);
        return Ok(result);
    }

    [HttpDelete("zabbix")]
    public async Task<IActionResult> ClearZabbix(CancellationToken ct)
    {
        await credentials.ClearZabbixAsync(ct);
        await mediator.Publish(new CredentialsChangedOccurred(CredentialTarget.Zabbix, CredentialAction.Cleared), ct);
        return NoContent();
    }

    // ── Telegram ─────────────────────────────────────────────────────────────

    [HttpPost("telegram")]
    public async Task<IActionResult> SaveTelegram([FromBody] StoreTelegramTokenRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BotToken))
            return BadRequest(new { error = "BotToken обов'язковий." });

        try
        {
            await credentials.StoreTelegramTokenAsync(request.BotToken, ct);
        }
        catch (CredentialProtectionException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }

        // TelegramBotService (T5.3) слухає CredentialsChangedOccurred(Telegram, Saved)
        // і перезапускає long-polling з новим токеном без перезапуску процесу.
        await mediator.Publish(new CredentialsChangedOccurred(CredentialTarget.Telegram, CredentialAction.Saved), ct);
        return NoContent();
    }

    [HttpDelete("telegram")]
    public async Task<IActionResult> ClearTelegram(CancellationToken ct)
    {
        await credentials.ClearTelegramAsync(ct);
        await mediator.Publish(new CredentialsChangedOccurred(CredentialTarget.Telegram, CredentialAction.Cleared), ct);
        return NoContent();
    }
}
