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
/// Result of the immediate connection check performed right after Save
/// (UX backlog #5: previously the user saved a token and got no feedback at
/// all — they had to wait up to 180s for the next poll cycle and hope
/// something showed up in Logs).
/// </summary>
public sealed record ZabbixTestResult(bool Success, string? Version, string? Error);

/// <summary>
/// T5.2 — GET/POST/DELETE for credentials (Zabbix/Telegram). Protected by
/// the same "Viewer" policy (AdminConsole-Admins) as the rest of the API —
/// the React "Settings → Credentials" page (Phase 6) will be a thin client
/// over these endpoints instead of the WPF credential modals.
///
/// There are no RDP endpoints here anymore: the backend service runs under a
/// dedicated domain account (DOMAIN\svc_adminconsole) with rights on the
/// target servers, so quser.exe authenticates via Kerberos in the process's
/// own context — no separate RDP credentials need to be stored.
///
/// Every successful Save/Clear publishes CredentialsChangedOccurred — the
/// same mechanism ZabbixPollerService uses to wake up from its wait (Phase 4)
/// and immediately apply the new credentials without waiting for the next
/// poll interval.
/// </summary>
public sealed class CredentialsController(
    CredentialStore credentials,
    IMediator mediator,
    ZabbixApiClient zabbixClient,
    IOptions<MonitoringSettings> monitoringSettings) : AdminConsoleControllerBase
{
    private const string ZabbixLogSource = "ZabbixPoller";

    /// <summary>
    /// Immediately checks the just-saved token/session via apiinfo.version +
    /// user.checkAuthentication (ZabbixApiClient.TestConnectionAsync — written
    /// earlier but never actually called before this). The result is
    /// published both to AppLogEntries (visible in Logs right away, no need
    /// to wait for a poll cycle) and returned in the HTTP response (visible
    /// in Settings right away).
    /// </summary>
    private async Task<ZabbixTestResult> TestAndLogAsync(string tokenForBearerHeader, CancellationToken ct)
    {
        var url = monitoringSettings.Value.ZabbixUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            const string msg = "ZabbixUrl is not configured in appsettings.json (Monitoring:ZabbixUrl) — cannot run the check.";
            await mediator.Publish(AppLogEntryOccurred.Warning(ZabbixLogSource, $"Zabbix: {msg}"), ct);
            return new ZabbixTestResult(false, null, msg);
        }

        var (success, version, error) = await zabbixClient.TestConnectionAsync(url, tokenForBearerHeader, ct);

        await mediator.Publish(success
            ? AppLogEntryOccurred.Success(ZabbixLogSource, $"Zabbix: connection verified successfully (version {version}).")
            : AppLogEntryOccurred.Warning(ZabbixLogSource, $"Zabbix: connection check failed — {error}"), ct);

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
            return BadRequest(new { error = "Token is required." });

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
            return BadRequest(new { error = "Username and Password are required." });

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
            const string msg = "Failed to authenticate with Zabbix — check the username/password.";
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
            return BadRequest(new { error = "BotToken is required." });

        try
        {
            await credentials.StoreTelegramTokenAsync(request.BotToken, ct);
        }
        catch (CredentialProtectionException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }

        // TelegramBotService (T5.3) listens for CredentialsChangedOccurred(Telegram, Saved)
        // and restarts long-polling with the new token without restarting the process.
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
