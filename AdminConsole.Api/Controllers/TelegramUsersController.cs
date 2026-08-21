using AdminConsole.Infrastructure.Telegram;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

public sealed record AddTelegramUserRequest(long ChatId, string? Username);

/// <summary>
/// T6.2 п.3 (Settings → Telegram Users) — CRUD для списку дозволених
/// користувачів Telegram-бота. Працює через TelegramAccessControlService,
/// а НЕ напряму через IAppSettingsRepository: сервіс тримає in-memory кеш
/// AllowedUsers, який TelegramBotService.IsAllowed() читає на кожне вхідне
/// повідомлення — обхід сервісу лишив би кеш застарілим до рестарту процесу.
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
}
