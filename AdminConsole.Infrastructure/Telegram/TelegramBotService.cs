using System.Collections.Concurrent;
using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Monitoring;
using AdminConsole.Infrastructure.Remote;
using AdminConsole.Infrastructure.Security;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AdminConsole.Infrastructure.Telegram;

/// <summary>
/// Telegram bot: read-only access to infrastructure status via buttons.
///
/// T5.3: IHostedService/BackgroundService in the same process (not a separate
/// service, not an HTTP client to our own API) — commands call
/// GetSnapshot()/GetActiveWindows() DIRECTLY on the Phase 4 Singleton services
/// (PingMonitorService, RdpMonitorService, UptimeTrackerService,
/// MaintenanceService), the same principle as in the old WPF app.
///
/// IRecipient&lt;X&gt; (WeakReferenceMessenger) → INotificationHandler&lt;XOccurred&gt;
/// (MediatR DI resolution, registered in Program.cs — the same Singleton
/// forwarding pattern used by the other multi-role Phase 4 services).
///
/// BackupMonitorService.GetSnapshot() no longer exists — BackupMonitorJob
/// (Hangfire, Phase 4) doesn't hold long-lived state between runs. Replaced
/// with a direct IBackupStateRepository.LoadAllAsync() call via IServiceScopeFactory
/// (Singleton → Scoped, same pattern). UserSettingsService.Current →
/// IAppSettingsRepository, same approach.
/// </summary>

/// <summary>
/// One pagination "screen": the screen key (so Offline isn't confused with
/// Incidents on a stale callback) + the already-built pages.
/// </summary>
internal sealed record TelegramPagedScreen(string ScreenKey, IReadOnlyList<string> Pages);

public sealed class TelegramBotService(
    IMediator                    mediator,
    ILogger<TelegramBotService>  logger,
    IServiceScopeFactory         scopeFactory,
    CredentialStore              credentials,
    TelegramAccessControlService access,
    PingMonitorService           pingMonitor,
    RdpMonitorService            rdpMonitor,
    UptimeTrackerService         uptimeTracker,
    MaintenanceService           maintenance,
    IOptions<List<ServerEntry>>  servers)
    : BackgroundService,
        INotificationHandler<PingBatchResultOccurred>,
        INotificationHandler<UptimeUpdatedOccurred>,
        INotificationHandler<RdpSessionsUpdatedOccurred>,
        INotificationHandler<CredentialsChangedOccurred>,
        INotificationHandler<BackupTransitionOccurred>
{
    private readonly IReadOnlyList<ServerEntry> _allServers = servers.Value.ToList().AsReadOnly();

    private readonly IReadOnlyList<ServerEntry> _terminalServers = servers.Value
        .Where(s => s.Group.Equals("Terminal Servers", StringComparison.OrdinalIgnoreCase))
        .ToList()
        .AsReadOnly();

    private readonly IReadOnlyDictionary<string, ServerEntry> _serverLookup = servers.Value
        .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private const string LogSource = "TelegramBot";
    private bool IsSingleTerminalServer => _terminalServers.Count == 1;

    // ── Push caches
    private readonly ConcurrentDictionary<string, PingStatus>       _pingCache = new();
    private readonly ConcurrentDictionary<string, RdpSessionsPayload> _rdpCache = new();

    private readonly ConcurrentDictionary<long, TelegramPagedScreen> _pagedScreens = new();
    private readonly ConcurrentDictionary<string, byte> _knownOpenIncidents = new();
    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private static string IncidentKey(DowntimeRecord r) => $"{r.ServerIp}|{r.FellAt.Ticks}";

    private readonly TelegramCallbackRegistry _serverPicker = new();

    private ITelegramBotClient?      _client;
    private CancellationTokenSource? _pollingCts;
    private Task?                    _pollingTask;
    private CancellationToken        _hostToken;

    // ── INotificationHandler — Push caches ────────────────────────────────────

    public Task Handle(PingBatchResultOccurred notification, CancellationToken ct)
    {
        foreach (var r in notification.Payload.Results)
            _pingCache[r.IP] = r.Status;
        return Task.CompletedTask;
    }

    public Task Handle(RdpSessionsUpdatedOccurred notification, CancellationToken ct)
    {
        _rdpCache[notification.Payload.ServerIp] = notification.Payload;
        return Task.CompletedTask;
    }

    public Task Handle(CredentialsChangedOccurred notification, CancellationToken ct)
    {
        if (notification.Target != CredentialTarget.Telegram) return Task.CompletedTask;
        if (notification.Action != CredentialAction.Saved) return Task.CompletedTask;

        // Not awaited here (Handle must stay fast) — run in the background, with its own error logging.
        _ = Task.Run(() => RestartPollingAsync(_hostToken));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Single source of infrastructure push notifications. Fires ONLY for a
    /// new, not-yet-seen open incident (!IsResolved). A server recovering
    /// is NEVER alerted on — it's just silently removed from
    /// _knownOpenIncidents, so the same server can generate another push
    /// the next time it goes down.
    /// </summary>
    public Task Handle(UptimeUpdatedOccurred notification, CancellationToken ct)
    {
        var currentlyOpen = notification.Snapshot.Where(r => !r.IsResolved).ToList();
        var currentKeys   = currentlyOpen.Select(IncidentKey).ToHashSet();

        foreach (var knownKey in _knownOpenIncidents.Keys.ToList())
            if (!currentKeys.Contains(knownKey))
                _knownOpenIncidents.TryRemove(knownKey, out _);

        foreach (var record in currentlyOpen)
        {
            string key = IncidentKey(record);
            if (_knownOpenIncidents.TryAdd(key, 0))
                _ = BroadcastIncidentAlertAsync(record);
        }

        return Task.CompletedTask;
    }

    private async Task BroadcastIncidentAlertAsync(DowntimeRecord record)
    {
        if (_client is null) return;

        string text = $"<b>🔴 SERVER OFFLINE</b>\n" +
                      $"{TelegramHtml.Escape(record.ServerName)} (<code>{TelegramHtml.Escape(record.ServerIp)}</code>)\n" +
                      $"Incident started: <code>{record.FellAt:dd.MM HH:mm:ss}</code>";

        var recipients = new List<long>();
        if (access.PrimaryAdminChatId is long adminId) recipients.Add(adminId);
        recipients.AddRange(access.GetAllowedChatIds());

        foreach (var chatId in recipients.Distinct())
        {
            try
            {
                await _client.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: _hostToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TelegramBotService: failed to send alert to chat_id={ChatId}", chatId);
            }
        }
    }

    public Task Handle(BackupTransitionOccurred notification, CancellationToken ct)
    {
        _ = BroadcastBackupAlertAsync(notification);
        return Task.CompletedTask;
    }

    private async Task BroadcastBackupAlertAsync(BackupTransitionOccurred message)
    {
        if (_client is null) return;

        // Bug fix (2026-08-22, backup service audit): Current can now also
        // be Unknown (BackupMonitorJob.OnConfirmedTransitionAsync started
        // pushing this alert for confirmed-Unknown transitions too, not just
        // Stale/Missing) — give it its own icon instead of falling through
        // to Stale's "⏰", which would misleadingly imply an old-but-present
        // backup file rather than "we can't reach this check at all".
        string icon = message.Current switch
        {
            BackupOutcome.Missing => "🚫",
            BackupOutcome.Unknown => "❓",
            _                     => "⏰",
        };
        string text = $"{icon} <b>BACKUP {message.Current.ToString().ToUpperInvariant()}</b>\n" +
                      $"{TelegramHtml.Escape(message.ServerName)} (<code>{message.Kind}</code>)\n" +
                      $"<i>Was: {message.Previous}</i>";

        var recipients = new List<long>();
        if (access.PrimaryAdminChatId is long adminId) recipients.Add(adminId);
        recipients.AddRange(access.GetAllowedChatIds());

        foreach (var chatId in recipients.Distinct())
        {
            try
            {
                await _client.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: _hostToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TelegramBotService: failed to send backup alert to chat_id={ChatId}", chatId);
            }
        }
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _hostToken = stoppingToken;

        // Audit Zone 1 (2026-08-22): InitializeAsync/RestartPollingAsync previously
        // ran with no try/catch at all at this level — RestartPollingAsync has a
        // try/finally internally (releases the lock), BUT no catch, so an exception
        // (e.g. from credentials.HasTelegramCredentials/GetTelegramToken) escaped
        // uncaught and took down the whole host.
        try
        {
            await access.InitializeAsync(stoppingToken);

            try
            {
                await credentials.LoadTelegramFromStoreAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "TelegramBotService: failed to load the token.");
            }

            foreach (var r in uptimeTracker.GetSnapshot().Where(r => !r.IsResolved))
                _knownOpenIncidents[IncidentKey(r)] = 0;

            await RestartPollingAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "TelegramBotService: fatal startup error — bot not started, application continues running.");
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }

        try
        {
            await _restartLock.WaitAsync();
            try { await StopPollingAsync(); }
            finally { _restartLock.Release(); }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TelegramBotService: error while stopping the bot.");
        }
    }

    private async Task RestartPollingAsync(CancellationToken hostToken)
    {
        await _restartLock.WaitAsync(hostToken);
        try
        {
            await StopPollingAsync();

            if (!credentials.HasTelegramCredentials)
            {
                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                    "Telegram bot token missing — bot not started."), hostToken);
                return;
            }

            var token  = credentials.GetTelegramToken();
            var client = new TelegramBotClient(token);

            try
            {
                var me = await client.GetMe();
                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    $"Telegram bot started: @{me.Username}"), hostToken);
            }
            catch (Exception ex)
            {
                await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                    $"Failed to connect to the Telegram API: {ex.Message}"), hostToken);
                return;
            }

            _client      = client;
            _pollingCts  = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            _pollingTask = PollLoopAsync(client, _pollingCts.Token);
        }
        finally
        {
            _restartLock.Release();
        }
    }

    private async Task StopPollingAsync()
    {
        if (_pollingCts is null) return;

        _pollingCts.Cancel();
        try { if (_pollingTask is not null) await _pollingTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "TelegramBotService: error stopping the polling loop."); }

        _pollingCts.Dispose();
        _pollingCts  = null;
        _pollingTask = null;
        _client      = null;
    }

    // ── Manual long-polling loop ──────────────────────────────────────────────

    private async Task PollLoopAsync(ITelegramBotClient client, CancellationToken ct)
    {
        int offset = 0;
        while (!ct.IsCancellationRequested)
        {
            Update[] updates;
            try
            {
                updates = await client.GetUpdates(offset, timeout: 30, cancellationToken: ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TelegramBotService: error fetching updates.");
                try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            foreach (var update in updates)
            {
                offset = update.Id + 1;
                try { await HandleUpdateAsync(client, update, ct); }
                catch (Exception ex)
                {
                    logger.LogError(ex, "TelegramBotService: error handling update {Id}", update.Id);
                }
            }
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        if (update.Message is { Text: not null } message)
            await HandleMessageAsync(client, message, ct);
        else if (update.CallbackQuery is not null)
            await HandleCallbackQueryAsync(client, update.CallbackQuery, ct);
    }

    // ── Text commands ──────────────────────────────────────────────────────

    private async Task HandleMessageAsync(ITelegramBotClient client, Message message, CancellationToken ct)
    {
        long   chatId   = message.Chat.Id;
        string username = message.From?.Username ?? message.From?.FirstName ?? "unknown";
        string text     = message.Text!.Trim();

        if (!access.CheckRateLimit(chatId))
        {
            if (access.IsAllowed(chatId))
                await client.SendMessage(chatId, "⏳ Too many requests, please wait a minute.",
                    parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            await HandleStartAsync(client, chatId, username, ct);
            return;
        }

        if (text.StartsWith("/claim_admin", StringComparison.OrdinalIgnoreCase))
        {
            await HandleClaimAdminAsync(client, chatId, text, ct);
            return;
        }

        if (!access.IsAllowed(chatId))
        {
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"Unauthorized: chat_id={chatId}, username=@{username}, text='{text}'"), ct);
            return;
        }

        switch (text)
        {
            case "📊 Status":            await SendStatusAsync(client, chatId, ct); return;
            case "🔴 Offline":           await SendOfflineListAsync(client, chatId, ct); return;
            case "⏱ Incidents":          await SendIncidentsListAsync(client, chatId, ct); return;
            case "🖥 RDP":               await SendRdpPickerAsync(client, chatId, ct); return;
            case "🔧 Maintenance":       await SendMaintenanceListAsync(client, chatId, ct); return;
            case "🏓 Ping":              await SendPingNowAsync(client, chatId, ct); return;
            case "💾 Backups":           await SendBackupsListAsync(client, chatId, ct); return;
            case "👥 Users":
                if (access.IsPrimaryAdmin(chatId)) await SendUsersListAsync(client, chatId, ct);
                return;
        }

        switch (text.Split(' ')[0].ToLowerInvariant())
        {
            case "/help":    await SendHelpAsync(client, chatId, ct); break;
            case "/status":  await SendStatusAsync(client, chatId, ct); break;
            case "/rdp":     await SendRdpPickerAsync(client, chatId, ct); break;
            case "/ping":    await SendPingNowAsync(client, chatId, ct); break;
            case "/backups": await SendBackupsListAsync(client, chatId, ct); break;
            case "/users":
                if (access.IsPrimaryAdmin(chatId)) await SendUsersListAsync(client, chatId, ct);
                break;
            default: await SendHelpAsync(client, chatId, ct); break;
        }
    }

    private async Task HandleStartAsync(ITelegramBotClient client, long chatId, string username, CancellationToken ct)
    {
        await access.RefreshUsernameAsync(chatId, username, ct);
        if (access.IsAllowed(chatId))
        {
            await client.SendMessage(chatId, "You already have access. /help — list of commands.",
                replyMarkup: BuildMainMenu(chatId), parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        if (!access.IsPrimaryAdminClaimed)
        {
            await client.SendMessage(chatId,
                "The bot doesn't have a Primary Admin yet. If that's you — enter /claim_admin <code>CODE</code>, " +
                "generated in Settings → Telegram.",
                parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        var result = await access.RegisterPendingRequestAsync(chatId, username, ct);

        if (result.Request is null)
        {
            if (result.CooldownRemaining is { } cooldown)
            {
                int minutesLeft = (int)Math.Ceiling(cooldown.TotalMinutes);
                await client.SendMessage(chatId,
                    $"⏳ Your previous request was denied. Try again in <b>{minutesLeft}</b> min.",
                    parseMode: ParseMode.Html, cancellationToken: ct);
            }
            else
            {
                await client.SendMessage(chatId,
                    "⏳ Too many pending access requests right now. Try again later.",
                    parseMode: ParseMode.Html, cancellationToken: ct);
            }
            return;
        }

        var request = result.Request;
        await client.SendMessage(chatId, "Access request sent to the administrator. Awaiting approval.",
            parseMode: ParseMode.Html, cancellationToken: ct);

        if (access.PrimaryAdminChatId is long adminId)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Approve", $"approve:{request.Id}"),
                    InlineKeyboardButton.WithCallbackData("❌ Deny", $"deny:{request.Id}")
                }
            });

            // username is fully attacker-controlled (a Telegram user picks their own
            // username) — must be escaped before landing in an admin-facing HTML message.
            await client.SendMessage(adminId,
                $"🔔 New access request: @{TelegramHtml.Escape(username)} (chat_id=<code>{chatId}</code>)",
                replyMarkup: keyboard, parseMode: ParseMode.Html, cancellationToken: ct);
        }
    }

    private async Task HandleClaimAdminAsync(ITelegramBotClient client, long chatId, string text, CancellationToken ct)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await client.SendMessage(chatId, "Usage: /claim_admin <code>CODE</code>",
                parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        bool success = await access.TryClaimAdminAsync(parts[1], chatId, ct);
        await client.SendMessage(chatId,
            success
                ? "✅ You are now bound as Primary Admin. /help — list of commands."
                : "❌ Invalid or expired code.",
            replyMarkup: success ? BuildMainMenu(chatId) : null,
            parseMode: ParseMode.Html, cancellationToken: ct);
    }

    // ── Inline callback (buttons) ──────────────────────────────────────────────

    private async Task HandleCallbackQueryAsync(ITelegramBotClient client, CallbackQuery query, CancellationToken ct)
    {
        if (query.Message is null)
        {
            logger.LogWarning(
                "TelegramBotService: CallbackQuery with no Message (id={QueryId}, data={Data}) — " +
                "likely a stale/deleted message.", query.Id, query.Data);

            try
            {
                // AnswerCallbackQuery shows a plain-text toast — Telegram doesn't
                // support parse modes there, so no HTML markup here.
                await client.AnswerCallbackQuery(query.Id,
                    "⚠️ This message is stale, reopen the screen from the menu.",
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TelegramBotService: failed to answer a stale callback.");
            }

            return;
        }

        long   chatId    = query.Message.Chat.Id;
        int    messageId = query.Message.MessageId;
        string data      = query.Data ?? string.Empty;

        if (!access.CheckRateLimit(chatId))
        {
            await client.AnswerCallbackQuery(query.Id, "Too many requests.", cancellationToken: ct);
            return;
        }

        if (data.StartsWith("approve:") || data.StartsWith("deny:"))
        {
            if (!access.IsPrimaryAdmin(chatId))
            {
                await client.AnswerCallbackQuery(query.Id, "No permission.", cancellationToken: ct);
                return;
            }

            int  id        = int.Parse(data.Split(':')[1]);
            bool isApprove = data.StartsWith("approve:");

            bool ok = isApprove ? await access.ApproveAsync(id, ct) : await access.DenyAsync(id, ct);

            if (!ok)
            {
                await client.AnswerCallbackQuery(query.Id, "This request is no longer valid.", cancellationToken: ct);
                await client.EditMessageText(chatId, messageId, "⚠️ This request has already been handled.",
                    parseMode: ParseMode.Html, cancellationToken: ct);
                return;
            }

            await client.AnswerCallbackQuery(query.Id,
                isApprove ? "Approved ✅" : "Denied ❌", cancellationToken: ct);
            await client.EditMessageText(chatId, messageId,
                isApprove ? "✅ Access approved." : "❌ Access denied.",
                parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        if (!access.IsAllowed(chatId))
        {
            await client.AnswerCallbackQuery(query.Id, "No access.", cancellationToken: ct);
            return;
        }

        await client.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        if (data.StartsWith("rdp_server:"))
        {
            int     shortId = int.Parse(data.Split(':')[1]);
            string? ip      = _serverPicker.Resolve(shortId);
            if (ip is null)
            {
                await client.EditMessageText(chatId, messageId, "⚠️ List is stale, reopen /rdp.",
                    parseMode: ParseMode.Html, cancellationToken: ct);
                return;
            }
            await EditWithRdpSessionsAsync(client, chatId, messageId, ip, ct);
        }
        else if (data == "back:status")
        {
            await EditWithStatusAsync(client, chatId, messageId, ct);
        }
        else if (data == "back:rdp_picker")
        {
            if (IsSingleTerminalServer)
                await EditWithRdpSessionsAsync(client, chatId, messageId, _terminalServers[0].IP, ct);
            else
                await EditWithRdpPickerAsync(client, chatId, messageId, ct);
        }
        else if (data.StartsWith("revoke:"))
        {
            if (!access.IsPrimaryAdmin(chatId)) return;
            long targetChatId = long.Parse(data.Split(':')[1]);
            await access.RevokeAsync(targetChatId, ct);
            await client.EditMessageText(chatId, messageId, $"🚫 Access for chat_id=<code>{targetChatId}</code> revoked.",
                parseMode: ParseMode.Html, cancellationToken: ct);
        }
        else if (data.StartsWith("page:"))
        {
            var    parts     = data.Split(':');
            string screenKey = parts[1];
            int    pageIndex = int.Parse(parts[2]);

            if (!_pagedScreens.TryGetValue(chatId, out var screen) || screen.ScreenKey != screenKey)
            {
                await client.EditMessageText(chatId, messageId,
                    "⚠️ List is stale, reopen the screen from the menu.", parseMode: ParseMode.Html, cancellationToken: ct);
                return;
            }

            pageIndex = Math.Clamp(pageIndex, 0, screen.Pages.Count - 1);
            await client.EditMessageText(chatId, messageId, screen.Pages[pageIndex],
                replyMarkup: BuildPaginationKeyboard(screenKey, pageIndex, screen.Pages.Count),
                parseMode: ParseMode.Html, cancellationToken: ct);
        }
    }

    // ── Building responses ───────────────────────────────────────────────────

    private ReplyKeyboardMarkup BuildMainMenu(long chatId)
    {
        var rows = new List<KeyboardButton[]>
        {
            new KeyboardButton[] { "📊 Status", "🔴 Offline" },
            new KeyboardButton[] { "⏱ Incidents", "🖥 RDP" },
            new KeyboardButton[] { "🔧 Maintenance", "🏓 Ping" }
        };

        rows.Add(access.IsPrimaryAdmin(chatId)
            ? new KeyboardButton[] { "💾 Backups", "👥 Users" }
            : new KeyboardButton[] { "💾 Backups" });

        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    private async Task SendHelpAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        string help = "<b>Available commands</b>\n" +
                      "<code>/status</code> — overall overview\n" +
                      "<code>/rdp</code> — RDP sessions per server\n" +
                      "<code>/ping</code> — ping all servers right now (real time)\n" +
                      "<code>/backups</code> — backup check status (Full/Diff)\n";
        if (access.IsPrimaryAdmin(chatId))
            help += "<code>/users</code> — manage access (Primary Admin only)\n";

        await client.SendMessage(chatId, help, replyMarkup: BuildMainMenu(chatId),
            parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task SendStatusAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        await client.SendMessage(chatId, await BuildStatusTextAsync(ct),
            replyMarkup: BuildStatusKeyboard(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task EditWithStatusAsync(ITelegramBotClient client, long chatId, int messageId, CancellationToken ct)
        => await client.EditMessageText(chatId, messageId, await BuildStatusTextAsync(ct),
            replyMarkup: BuildStatusKeyboard(), parseMode: ParseMode.Html, cancellationToken: ct);

    // ── /ping — direct real-time poll of all servers ──────────────

    private async Task SendPingNowAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        if (!access.TryConsumePingCooldown(chatId, out var remaining))
        {
            await client.SendMessage(chatId,
                $"⏳ Please wait {Math.Ceiling(remaining.TotalSeconds)}s before the next ping.",
                parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        var placeholder = await client.SendMessage(chatId, "🏓 Pinging all servers…",
            parseMode: ParseMode.Html, cancellationToken: ct);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<PingResult> results;
        try
        {
            results = await pingMonitor.PingAllNowAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TelegramBotService: error running /ping.");
            await client.EditMessageText(chatId, placeholder.MessageId,
                "❌ Error while pinging servers.", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }
        sw.Stop();

        var lines = results
            .GroupBy(r => r.Group)
            .OrderBy(g => g.Key)
            .SelectMany(BuildGroupLines)
            .ToList();

        int online  = results.Count(r => r.Status == PingStatus.Online);
        int offline = results.Count(r => r.Status == PingStatus.Offline);

        string header = $"<b>🏓 Ping results</b> (<code>{results.Count}</code> servers, <code>{sw.Elapsed.TotalSeconds:F1}s</code>)\n" +
                         $"✅ {online} online   🔴 {offline} offline\n\n";

        var pages = TelegramTextChunker.BuildPages(lines, header);
        _pagedScreens[chatId] = new TelegramPagedScreen("ping", pages);

        await client.EditMessageText(chatId, placeholder.MessageId, pages[0],
            replyMarkup: BuildPaginationKeyboard("ping", 0, pages.Count),
            parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private static IEnumerable<string> BuildGroupLines(IGrouping<string, PingResult> group)
    {
        yield return $"<b>— {TelegramHtml.Escape(group.Key)} —</b>";
        foreach (var r in group.OrderBy(x => x.Name))
        {
            string icon = r.Status switch
            {
                PingStatus.Online  => "✅",
                PingStatus.Offline => "🔴",
                _                  => "⏳"
            };
            string latency = r.Status == PingStatus.Online && r.LatencyMs is not null
                ? $" — <code>{r.LatencyMs} ms</code>"
                : string.Empty;

            yield return $"{icon} {TelegramHtml.Escape(r.Name)} (<code>{TelegramHtml.Escape(r.IP)}</code>){latency}";
        }
    }

    private async Task<string> BuildStatusTextAsync(CancellationToken ct)
    {
        var pingSnapshot = pingMonitor.GetSnapshot();
        int offline = pingSnapshot.Values.Count(s => s == PingStatus.Offline);
        int online  = pingSnapshot.Values.Count(s => s == PingStatus.Online);

        int openIncidents = uptimeTracker.GetSnapshot().Count(r => !r.IsResolved);

        // Audit fix items 4/5: this used to be counted directly from rdpMonitor.GetSnapshot()
        // without checking the toggle — /status showed the last known (stale)
        // RDP session count even after RDP monitoring was disabled in Settings.
        // Same class of bug as on Overview for Zabbix — same check
        // pattern already used in SendRdpPickerAsync/SendBackupsListAsync.
        bool rdpEnabled = (await GetAppSettingsAsync(ct)).RdpMonitoringEnabled;
        string rdpLine = rdpEnabled
            ? $"🖥 RDP sessions (active): <code>{rdpMonitor.GetSnapshot().Values.Sum(list => list.Count(s => s.State == RdpSessionState.Active))}</code>"
            : "🖥 RDP sessions: <i>monitoring disabled in Settings</i>";

        int activeMaintenance = maintenance.GetActiveWindows().Count;

        return $"<b>📊 Infrastructure status</b>\n" +
               $"✅ Online: <code>{online}</code>\n" +
               $"🔴 Offline: <code>{offline}</code>\n" +
               $"⏱ Open incidents: <code>{openIncidents}</code>\n" +
               $"{rdpLine}\n" +
               $"🔧 Active maintenance windows: <code>{activeMaintenance}</code>";
    }

    private static InlineKeyboardMarkup BuildStatusKeyboard() =>
        new(Array.Empty<InlineKeyboardButton[]>());

    private async Task SendRdpPickerAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        if (!(await GetAppSettingsAsync(ct)).RdpMonitoringEnabled)
        {
            await client.SendMessage(chatId, "🖥 RDP session monitoring is currently disabled in Settings.\n",
                parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        if (IsSingleTerminalServer)
        {
            await SendRdpSessionsDirectAsync(client, chatId, _terminalServers[0].IP, ct);
            return;
        }

        await client.SendMessage(chatId, "Choose a server:",
            replyMarkup: BuildRdpPickerKeyboard(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task SendRdpSessionsDirectAsync(
        ITelegramBotClient client, long chatId, string serverIp, CancellationToken ct)
    {
        var snapshot = rdpMonitor.GetSnapshot();
        var sessions = snapshot.TryGetValue(serverIp, out var list) ? list : [];

        var lines = sessions.Count == 0
            ? new List<string> { "No active sessions." }
            : sessions.Select(s => $"<b>{TelegramHtml.Escape(s.Username)}</b> — {s.State} (logon: <code>{s.LogonTime}</code>)").ToList();

        var pages = TelegramTextChunker.BuildPages(lines, header: "<b>🖥 Sessions</b>\n");
        _pagedScreens[chatId] = new TelegramPagedScreen("rdp_direct", pages);

        await client.SendMessage(chatId, pages[0],
            replyMarkup: BuildPaginationKeyboard("rdp_direct", 0, pages.Count),
            parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task EditWithRdpPickerAsync(ITelegramBotClient client, long chatId, int messageId, CancellationToken ct)
    {
        if (!(await GetAppSettingsAsync(ct)).RdpMonitoringEnabled)
        {
            await client.EditMessageText(chatId, messageId,
                "🖥 RDP session monitoring is currently disabled in Settings.", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        await client.EditMessageText(chatId, messageId, "Choose a server:",
            replyMarkup: BuildRdpPickerKeyboard(), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private InlineKeyboardMarkup BuildRdpPickerKeyboard()
    {
        var rows = _terminalServers
            .Select(s => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    s.Name, $"rdp_server:{_serverPicker.Register(s.IP)}")
            })
            .ToArray();

        return new InlineKeyboardMarkup(rows);
    }

    private async Task EditWithRdpSessionsAsync(
        ITelegramBotClient client, long chatId, int messageId, string serverIp, CancellationToken ct)
    {
        if (!(await GetAppSettingsAsync(ct)).RdpMonitoringEnabled)
        {
            await client.EditMessageText(chatId, messageId,
                "🖥 RDP session monitoring is currently disabled in Settings.", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        var snapshot = rdpMonitor.GetSnapshot();
        var sessions = snapshot.TryGetValue(serverIp, out var list) ? list : [];

        var lines = sessions.Count == 0
            ? new List<string> { "No active sessions." }
            : sessions.Select(s => $"<b>{TelegramHtml.Escape(s.Username)}</b> — {s.State} (logon: <code>{s.LogonTime}</code>)").ToList();

        var pages = TelegramTextChunker.BuildPages(lines, header: "<b>🖥 Sessions</b>\n");

        var keyboard = IsSingleTerminalServer
            ? null
            : new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("◀ Back", "back:rdp_picker") }
            });

        await client.EditMessageText(chatId, messageId, pages[0], replyMarkup: keyboard,
            parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task SendUsersListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        var users = access.GetAllowedUsers();

        if (users.Count == 0)
        {
            await client.SendMessage(chatId, "👥 No allowed users.", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        // Button labels are always plain text in Telegram — no HTML parsing there,
        // so no escaping needed for the keyboard (only for message text).
        var rows = users
            .Select(u => new[] { InlineKeyboardButton.WithCallbackData(
                $"🚫 @{u.Username} ({u.ChatId})", $"revoke:{u.ChatId}") })
            .ToArray();

        await client.SendMessage(chatId, "<b>👥 Allowed users</b>",
            replyMarkup: new InlineKeyboardMarkup(rows), parseMode: ParseMode.Html, cancellationToken: ct);
    }

    // ── Offline / Incidents / Maintenance (with pagination) ──────────────────

    private async Task SendOfflineListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        var snapshot = pingMonitor.GetSnapshot();

        var lines = _allServers
            .Where(s => snapshot.TryGetValue(s.IP, out var status) && status == PingStatus.Offline)
            .Select(s => $"🔴 <b>{TelegramHtml.Escape(s.Name)}</b> (<code>{TelegramHtml.Escape(s.IP)}</code>) — {TelegramHtml.Escape(s.Group)}")
            .ToList();

        if (lines.Count == 0) lines.Add("No offline servers. ✅");

        await SendPagedScreenAsync(client, chatId, "offline", "<b>🔴 Offline servers</b>\n", lines, ct);
    }

    private async Task SendIncidentsListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        var openIncidents = uptimeTracker.GetSnapshot()
            .Where(r => !r.IsResolved)
            .OrderByDescending(r => r.FellAt)
            .ToList();

        var lines = openIncidents
            .Select(r => $"⏱ <b>{TelegramHtml.Escape(r.ServerName)}</b> (<code>{TelegramHtml.Escape(r.ServerIp)}</code>)\n" +
                         $"   Went down: <code>{r.FellAt:dd.MM HH:mm}</code> — ongoing for <code>{r.DurationDisplay}</code>")
            .ToList();

        if (lines.Count == 0) lines.Add("No open incidents. ✅");

        await SendPagedScreenAsync(client, chatId, "incidents", "<b>⏱ Open incidents</b>\n", lines, ct);
    }

    private async Task SendMaintenanceListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        var windows = maintenance.GetActiveWindows();
        var lines   = TelegramMessageFormatter.BuildMaintenanceBlocks(windows);
        string header = $"<b>🔧 Maintenance</b> ({windows.Count} active)\n\n";

        await SendPagedScreenAsync(client, chatId, "maintenance", header, lines, ct);
    }

    /// <summary>
    /// Backup Verification: a live snapshot straight from IBackupStateRepository
    /// (not GetSnapshot() — BackupMonitorJob, Hangfire, doesn't hold state between
    /// runs; the data is the same, just the source is the DB rather than an in-memory cache).
    /// </summary>
    private async Task SendBackupsListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        if (!(await GetAppSettingsAsync(ct)).BackupMonitoringEnabled)
        {
            await client.SendMessage(chatId,
                "💾 Backup monitoring is currently disabled in Settings.\nBackup status data is not being updated.",
                parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        var states = await GetBackupSnapshotAsync(ct);
        var lines  = TelegramMessageFormatter.BuildBackupBlocks(states,
            host => _serverLookup.TryGetValue(host, out var entry) && maintenance.IsUnderMaintenance(entry.IP, entry.Group));

        await SendPagedScreenAsync(client, chatId, "backups", "<b>💾 Backup status</b>\n\n", lines, ct);
    }

    private async Task SendPagedScreenAsync(
        ITelegramBotClient client, long chatId, string screenKey, string header,
        List<string> lines, CancellationToken ct)
    {
        var pages = TelegramTextChunker.BuildPages(lines, header);
        _pagedScreens[chatId] = new TelegramPagedScreen(screenKey, pages);

        await client.SendMessage(chatId, pages[0],
            replyMarkup: BuildPaginationKeyboard(screenKey, 0, pages.Count),
            parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private static InlineKeyboardMarkup BuildPaginationKeyboard(string screenKey, int pageIndex, int pageCount)
    {
        if (pageCount <= 1)
            return new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>());

        var row = new List<InlineKeyboardButton>();

        if (pageIndex > 0)
            row.Add(InlineKeyboardButton.WithCallbackData("◀ Back", $"page:{screenKey}:{pageIndex - 1}"));

        if (pageIndex < pageCount - 1)
            row.Add(InlineKeyboardButton.WithCallbackData("Next ▶", $"page:{screenKey}:{pageIndex + 1}"));

        return new InlineKeyboardMarkup(new[] { row.ToArray() });
    }

    // ── Scoped access to Phase 2 repositories (Singleton → Scoped) ──────────

    private async Task<AppSettings> GetAppSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>().GetAsync(ct);
    }

    private async Task<IReadOnlyList<BackupCheckState>> GetBackupSnapshotAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBackupStateRepository>().LoadAllAsync(ct);
    }

    public override void Dispose()
    {
        _restartLock.Dispose();
        base.Dispose();
    }
}
