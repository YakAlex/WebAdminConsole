using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Telegram;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

public sealed record ClaimCodeResponse(string Code, DateTimeOffset ExpiresAt);
public sealed record TelegramPendingStatusResponse(
    IReadOnlyList<TelegramPendingRequest> Pending,
    bool                                  IsPrimaryAdminClaimed);

/// <summary>
/// T6.2 item 3 (Settings → Telegram Users) — CRUD for the list of allowed
/// Telegram bot users. Goes through TelegramAccessControlService rather than
/// IAppSettingsRepository directly: the service keeps an in-memory
/// AllowedUsers cache that TelegramBotService.IsAllowed() reads on every
/// incoming message — bypassing the service would leave the cache stale
/// until the next process restart.
///
/// Audit fix (2026-08-22, item 2 of the migration gap report): claim code +
/// pending requests follow the same principle as Maintenance (item 1): the
/// service layer (GenerateClaimCode/GetAllPending/ApproveAsync/DenyAsync)
/// already existed and was used by the bot, it just had no REST layer over
/// it. Unlike WPF (where the desktop Settings only had Deny — Approve was
/// only available via inline buttons in Telegram itself), both are
/// deliberately exposed here — agreed with the user: the entire web app
/// already sits behind the same Windows AD-group gate that Telegram
/// approval already trusts.
/// </summary>
public sealed class TelegramUsersController(TelegramAccessControlService accessControl) : AdminConsoleControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<TelegramAllowedUserView>> Get() =>
        Ok(accessControl.GetAllowedUsers());

    [HttpDelete("{chatId:long}")]
    public async Task<IActionResult> Remove(long chatId, CancellationToken ct)
    {
        bool removed = await accessControl.RevokeAsync(chatId, ct);
        return removed ? NoContent() : NotFound();
    }

    /// <summary>
    /// The admin still sends /claim_admin &lt;code&gt; in Telegram themselves —
    /// that part of the flow doesn't change; this only generates the code,
    /// which previously only WPF could do.
    /// </summary>
    [HttpPost("claim-code")]
    public ActionResult<ClaimCodeResponse> GenerateClaimCode()
    {
        if (accessControl.IsPrimaryAdminClaimed)
            return BadRequest(new { error = "Primary Admin is already claimed." });

        var (code, expiresAt) = accessControl.GenerateClaimCode();
        return Ok(new ClaimCodeResponse(code, expiresAt));
    }

    [HttpGet("pending")]
    public ActionResult<TelegramPendingStatusResponse> GetPending() =>
        Ok(new TelegramPendingStatusResponse(accessControl.GetAllPending(), accessControl.IsPrimaryAdminClaimed));

    [HttpPost("pending/{id:int}/approve")]
    public async Task<IActionResult> ApprovePending(int id, CancellationToken ct)
    {
        bool approved = await accessControl.ApproveAsync(id, ct);
        return approved ? NoContent() : NotFound(new { error = $"Pending request #{id} not found (may already have been processed)." });
    }

    [HttpPost("pending/{id:int}/deny")]
    public async Task<IActionResult> DenyPending(int id, CancellationToken ct)
    {
        bool denied = await accessControl.DenyAsync(id, ct);
        return denied ? NoContent() : NotFound(new { error = $"Pending request #{id} not found (may already have been processed)." });
    }
}
