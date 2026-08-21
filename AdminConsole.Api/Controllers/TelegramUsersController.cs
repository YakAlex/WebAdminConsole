using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Telegram;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

public sealed record AddTelegramUserRequest(long ChatId, string? Username);
public sealed record ClaimCodeResponse(string Code, DateTimeOffset ExpiresAt);
public sealed record TelegramPendingStatusResponse(
    IReadOnlyList<TelegramPendingRequest> Pending,
    bool                                  IsPrimaryAdminClaimed);

/// <summary>
/// T6.2 п.3 (Settings → Telegram Users) — CRUD для списку дозволених
/// користувачів Telegram-бота. Працює через TelegramAccessControlService,
/// а НЕ напряму через IAppSettingsRepository: сервіс тримає in-memory кеш
/// AllowedUsers, який TelegramBotService.IsAllowed() читає на кожне вхідне
/// повідомлення — обхід сервісу лишив би кеш застарілим до рестарту процесу.
///
/// Аудит-фікс (2026-08-22, п.2 звіту про прогалини міграції): claim-code +
/// pending-запити — той самий принцип, що й Maintenance (п.1): сервісний
/// шар (GenerateClaimCode/GetAllPending/ApproveAsync/DenyAsync) уже існував
/// і використовувався ботом, просто без REST-шару над ним. На відміну від
/// WPF (де десктопні Settings мали лише Deny, Approve — тільки inline-кнопки
/// в самому Telegram), тут навмисно додано ОБОХ — узгоджено з користувачем:
/// весь веб-застосунок і так за тим самим Windows AD-group гейтом, що
/// й довіра до Telegram-схвалення.
/// </summary>
public sealed class TelegramUsersController(TelegramAccessControlService accessControl) : AdminConsoleControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<TelegramAllowedUserView>> Get() =>
        Ok(accessControl.GetAllowedUsers());

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddTelegramUserRequest request, CancellationToken ct)
    {
        if (request.ChatId == 0)
            return BadRequest(new { error = "ChatId обов'язковий." });

        await accessControl.AddAllowedUserAsync(request.ChatId, request.Username, ct);
        return NoContent();
    }

    [HttpDelete("{chatId:long}")]
    public async Task<IActionResult> Remove(long chatId, CancellationToken ct)
    {
        bool removed = await accessControl.RevokeAsync(chatId, ct);
        return removed ? NoContent() : NotFound();
    }

    /// <summary>
    /// Адмін і далі сам надсилає /claim_admin &lt;код&gt; в Telegram — ця
    /// частина флоу не змінюється, тут лише генерація коду, яку раніше
    /// міг зробити тільки WPF.
    /// </summary>
    [HttpPost("claim-code")]
    public ActionResult<ClaimCodeResponse> GenerateClaimCode()
    {
        if (accessControl.IsPrimaryAdminClaimed)
            return BadRequest(new { error = "Primary Admin уже прив'язано." });

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
        return approved ? NoContent() : NotFound(new { error = $"Pending-запит #{id} не знайдено (можливо, вже оброблено)." });
    }

    [HttpPost("pending/{id:int}/deny")]
    public async Task<IActionResult> DenyPending(int id, CancellationToken ct)
    {
        bool denied = await accessControl.DenyAsync(id, ct);
        return denied ? NoContent() : NotFound(new { error = $"Pending-запит #{id} не знайдено (можливо, вже оброблено)." });
    }
}
