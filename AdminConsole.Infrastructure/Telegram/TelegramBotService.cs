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
using Telegram.Bot.Types.ReplyMarkups;

namespace AdminConsole.Infrastructure.Telegram;

/// <summary>
/// Telegram-бот: read-only доступ до статусу інфраструктури через кнопки.
///
/// T5.3: IHostedService/BackgroundService у тому самому процесі (не окремий
/// сервіс, не HTTP-клієнт до власного API) — команди викликають
/// GetSnapshot()/GetActiveWindows() НАПРЯМУ з Singleton-сервісів Фази 4
/// (PingMonitorService, RdpMonitorService, UptimeTrackerService,
/// MaintenanceService), той самий принцип, що й у старому WPF.
///
/// IRecipient&lt;X&gt; (WeakReferenceMessenger) → INotificationHandler&lt;XOccurred&gt;
/// (DI-резолв MediatR, реєстрація в Program.cs — той самий Singleton-
/// forwarding патерн, що інші багаторольові Фаза-4-сервіси).
///
/// BackupMonitorService.GetSnapshot() більше не існує — BackupMonitorJob
/// (Hangfire, Фаза 4) не тримає довгоживучий стан між запусками. Замінено
/// на прямий IBackupStateRepository.LoadAllAsync() через IServiceScopeFactory
/// (Singleton → Scoped, той самий патерн). UserSettingsService.Current →
/// IAppSettingsRepository, той самий підхід.
/// </summary>

/// <summary>
/// Один "екран" пагінації: ключ екрану (щоб не плутати Офлайн з Інцидентами
/// при stale callback) + вже побудовані сторінки.
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

    // ── Push-кеші
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

    // ── INotificationHandler — Push-кеші ────────────────────────────────────

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

        // Не awaited тут (Handle має лишатись швидким) — фоново, з власним лог-обробленням помилок.
        _ = Task.Run(() => RestartPollingAsync(_hostToken));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Єдине джерело push-сповіщень про інфраструктуру. Спрацьовує ЛИШЕ на
    /// новий, ще не бачений відкритий інцидент (!IsResolved). Відновлення
    /// сервера — НІКОЛИ не алертиться, лише мовчки прибирається з
    /// _knownOpenIncidents, щоб той самий сервер міг знову згенерувати push
    /// при наступному падінні.
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

        string text = $"🔴 СЕРВЕР ОФЛАЙН\n" +
                      $"{record.ServerName} ({record.ServerIp})\n" +
                      $"Початок інциденту: {record.FellAt:dd.MM HH:mm:ss}";

        var recipients = new List<long>();
        if (access.PrimaryAdminChatId is long adminId) recipients.Add(adminId);
        recipients.AddRange(access.GetAllowedChatIds());

        foreach (var chatId in recipients.Distinct())
        {
            try
            {
                await _client.SendMessage(chatId, text, cancellationToken: _hostToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TelegramBotService: не вдалось надіслати alert у chat_id={ChatId}", chatId);
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

        string icon = message.Current == BackupOutcome.Missing ? "🚫" : "⏰";
        string text = $"{icon} БЕКАП {message.Current.ToString().ToUpperInvariant()}\n" +
                      $"{message.ServerName} ({message.Kind})\n" +
                      $"Було: {message.Previous}";

        var recipients = new List<long>();
        if (access.PrimaryAdminChatId is long adminId) recipients.Add(adminId);
        recipients.AddRange(access.GetAllowedChatIds());

        foreach (var chatId in recipients.Distinct())
        {
            try
            {
                await _client.SendMessage(chatId, text, cancellationToken: _hostToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TelegramBotService: не вдалось надіслати backup alert у chat_id={ChatId}", chatId);
            }
        }
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _hostToken = stoppingToken;

        // Аудит Зона 1 (2026-08-22): InitializeAsync/RestartPollingAsync раніше
        // виконувались без жодного try/catch на цьому рівні — RestartPollingAsync
        // усередині має try/finally (звільняє лок), АЛЕ без catch, тож виняток
        // (напр. з credentials.HasTelegramCredentials/GetTelegramToken) летів
        // далі необхопленим і клав увесь хост.
        try
        {
            await access.InitializeAsync(stoppingToken);

            try
            {
                await credentials.LoadTelegramFromStoreAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "TelegramBotService: не вдалось завантажити токен.");
            }

            foreach (var r in uptimeTracker.GetSnapshot().Where(r => !r.IsResolved))
                _knownOpenIncidents[IncidentKey(r)] = 0;

            await RestartPollingAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "TelegramBotService: критична помилка старту — бот не запущено, застосунок продовжує працювати.");
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
            logger.LogError(ex, "TelegramBotService: помилка під час зупинки бота.");
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
                    "Telegram bot token відсутній — бот не запущений."), hostToken);
                return;
            }

            var token  = credentials.GetTelegramToken();
            var client = new TelegramBotClient(token);

            try
            {
                var me = await client.GetMe();
                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    $"Telegram bot запущено: @{me.Username}"), hostToken);
            }
            catch (Exception ex)
            {
                await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                    $"Не вдалося підключитись до Telegram API: {ex.Message}"), hostToken);
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
        catch (Exception ex) { logger.LogWarning(ex, "TelegramBotService: помилка зупинки полінгу."); }

        _pollingCts.Dispose();
        _pollingCts  = null;
        _pollingTask = null;
        _client      = null;
    }

    // ── Ручний long-polling цикл ──────────────────────────────────────────────

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
                logger.LogWarning(ex, "TelegramBotService: помилка отримання updates.");
                try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            foreach (var update in updates)
            {
                offset = update.Id + 1;
                try { await HandleUpdateAsync(client, update, ct); }
                catch (Exception ex)
                {
                    logger.LogError(ex, "TelegramBotService: помилка обробки update {Id}", update.Id);
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

    // ── Текстові команди ──────────────────────────────────────────────────────

    private async Task HandleMessageAsync(ITelegramBotClient client, Message message, CancellationToken ct)
    {
        long   chatId   = message.Chat.Id;
        string username = message.From?.Username ?? message.From?.FirstName ?? "unknown";
        string text     = message.Text!.Trim();

        if (!access.CheckRateLimit(chatId))
        {
            if (access.IsAllowed(chatId))
                await client.SendMessage(chatId, "⏳ Забагато запитів, зачекайте хвилину.", cancellationToken: ct);
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
            case "📊 Статус":            await SendStatusAsync(client, chatId, ct); return;
            case "🔴 Офлайн":            await SendOfflineListAsync(client, chatId, ct); return;
            case "⏱ Інциденти":          await SendIncidentsListAsync(client, chatId, ct); return;
            case "🖥 RDP":               await SendRdpPickerAsync(client, chatId, ct); return;
            case "🔧 Обслуговування":     await SendMaintenanceListAsync(client, chatId, ct); return;
            case "🏓 Пінг":              await SendPingNowAsync(client, chatId, ct); return;
            case "💾 Бекапи":            await SendBackupsListAsync(client, chatId, ct); return;
            case "👥 Користувачі":
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
            await client.SendMessage(chatId, "Ви вже маєте доступ. /help — список команд.",
                replyMarkup: BuildMainMenu(chatId), cancellationToken: ct);
            return;
        }

        if (!access.IsPrimaryAdminClaimed)
        {
            await client.SendMessage(chatId,
                "Бот ще не має Primary Admin. Якщо це ви — введіть /claim_admin <код>, " +
                "згенерований у розділі Налаштування → Telegram.",
                cancellationToken: ct);
            return;
        }

        var result = await access.RegisterPendingRequestAsync(chatId, username, ct);

        if (result.Request is null)
        {
            if (result.CooldownRemaining is { } cooldown)
            {
                int minutesLeft = (int)Math.Ceiling(cooldown.TotalMinutes);
                await client.SendMessage(chatId,
                    $"⏳ Ваш попередній запит було відхилено. Спробуйте ще раз через {minutesLeft} хв.",
                    cancellationToken: ct);
            }
            else
            {
                await client.SendMessage(chatId,
                    "⏳ Забагато очікуючих запитів доступу зараз. Спробуйте пізніше.",
                    cancellationToken: ct);
            }
            return;
        }

        var request = result.Request;
        await client.SendMessage(chatId, "Запит на доступ надіслано адміністратору. Очікуйте підтвердження.",
            cancellationToken: ct);

        if (access.PrimaryAdminChatId is long adminId)
        {
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Дозволити", $"approve:{request.Id}"),
                    InlineKeyboardButton.WithCallbackData("❌ Відхилити", $"deny:{request.Id}")
                }
            });

            await client.SendMessage(adminId,
                $"🔔 Новий запит доступу: @{username} (chat_id={chatId})",
                replyMarkup: keyboard, cancellationToken: ct);
        }
    }

    private async Task HandleClaimAdminAsync(ITelegramBotClient client, long chatId, string text, CancellationToken ct)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await client.SendMessage(chatId, "Використання: /claim_admin <код>", cancellationToken: ct);
            return;
        }

        bool success = await access.TryClaimAdminAsync(parts[1], chatId, ct);
        await client.SendMessage(chatId,
            success
                ? "✅ Ви прив'язані як Primary Admin. /help — список команд."
                : "❌ Невірний або протермінований код.",
            replyMarkup: success ? BuildMainMenu(chatId) : null,
            cancellationToken: ct);
    }

    // ── Inline callback (кнопки) ──────────────────────────────────────────────

    private async Task HandleCallbackQueryAsync(ITelegramBotClient client, CallbackQuery query, CancellationToken ct)
    {
        if (query.Message is null)
        {
            logger.LogWarning(
                "TelegramBotService: CallbackQuery без Message (id={QueryId}, data={Data}) — " +
                "ймовірно, застаріле/видалене повідомлення.", query.Id, query.Data);

            try
            {
                await client.AnswerCallbackQuery(query.Id,
                    "⚠️ Це повідомлення застаріло, відкрийте екран знову через меню.",
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TelegramBotService: не вдалось відповісти на застарілий callback.");
            }

            return;
        }

        long   chatId    = query.Message.Chat.Id;
        int    messageId = query.Message.MessageId;
        string data      = query.Data ?? string.Empty;

        if (!access.CheckRateLimit(chatId))
        {
            await client.AnswerCallbackQuery(query.Id, "Забагато запитів.", cancellationToken: ct);
            return;
        }

        if (data.StartsWith("approve:") || data.StartsWith("deny:"))
        {
            if (!access.IsPrimaryAdmin(chatId))
            {
                await client.AnswerCallbackQuery(query.Id, "Немає прав.", cancellationToken: ct);
                return;
            }

            int  id        = int.Parse(data.Split(':')[1]);
            bool isApprove = data.StartsWith("approve:");

            bool ok = isApprove ? await access.ApproveAsync(id, ct) : await access.DenyAsync(id, ct);

            if (!ok)
            {
                await client.AnswerCallbackQuery(query.Id, "Запит вже неактуальний.", cancellationToken: ct);
                await client.EditMessageText(chatId, messageId, "⚠️ Цей запит вже опрацьовано раніше.",
                    cancellationToken: ct);
                return;
            }

            await client.AnswerCallbackQuery(query.Id,
                isApprove ? "Дозволено ✅" : "Відхилено ❌", cancellationToken: ct);
            await client.EditMessageText(chatId, messageId,
                isApprove ? "✅ Доступ дозволено." : "❌ Доступ відхилено.", cancellationToken: ct);
            return;
        }

        if (!access.IsAllowed(chatId))
        {
            await client.AnswerCallbackQuery(query.Id, "Немає доступу.", cancellationToken: ct);
            return;
        }

        await client.AnswerCallbackQuery(query.Id, cancellationToken: ct);

        if (data.StartsWith("rdp_server:"))
        {
            int     shortId = int.Parse(data.Split(':')[1]);
            string? ip      = _serverPicker.Resolve(shortId);
            if (ip is null)
            {
                await client.EditMessageText(chatId, messageId, "⚠️ Список застарів, відкрийте /rdp знову.", cancellationToken: ct);
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
            await client.EditMessageText(chatId, messageId, $"🚫 Доступ для chat_id={targetChatId} відкликано.", cancellationToken: ct);
        }
        else if (data.StartsWith("page:"))
        {
            var    parts     = data.Split(':');
            string screenKey = parts[1];
            int    pageIndex = int.Parse(parts[2]);

            if (!_pagedScreens.TryGetValue(chatId, out var screen) || screen.ScreenKey != screenKey)
            {
                await client.EditMessageText(chatId, messageId,
                    "⚠️ Список застарів, відкрийте екран знову через меню.", cancellationToken: ct);
                return;
            }

            pageIndex = Math.Clamp(pageIndex, 0, screen.Pages.Count - 1);
            await client.EditMessageText(chatId, messageId, screen.Pages[pageIndex],
                replyMarkup: BuildPaginationKeyboard(screenKey, pageIndex, screen.Pages.Count),
                cancellationToken: ct);
        }
    }

    // ── Побудова відповідей ───────────────────────────────────────────────────

    private ReplyKeyboardMarkup BuildMainMenu(long chatId)
    {
        var rows = new List<KeyboardButton[]>
        {
            new KeyboardButton[] { "📊 Статус", "🔴 Офлайн" },
            new KeyboardButton[] { "⏱ Інциденти", "🖥 RDP" },
            new KeyboardButton[] { "🔧 Обслуговування", "🏓 Пінг" }
        };

        rows.Add(access.IsPrimaryAdmin(chatId)
            ? new KeyboardButton[] { "💾 Бекапи", "👥 Користувачі" }
            : new KeyboardButton[] { "💾 Бекапи" });

        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
    }

    private async Task SendHelpAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        string help = "Доступні команди:\n" +
                      "/status — загальний огляд\n" +
                      "/rdp — RDP-сесії по серверах\n" +
                      "/ping — пінгувати всі сервери прямо зараз (реальний час)\n" +
                      "/backups — статус перевірок бекапів (Full/Diff)\n";
        if (access.IsPrimaryAdmin(chatId))
            help += "/users — керування доступом (тільки Primary Admin)\n";

        await client.SendMessage(chatId, help, replyMarkup: BuildMainMenu(chatId), cancellationToken: ct);
    }

    private async Task SendStatusAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        await client.SendMessage(chatId, await BuildStatusTextAsync(ct),
            replyMarkup: BuildStatusKeyboard(), cancellationToken: ct);
    }

    private async Task EditWithStatusAsync(ITelegramBotClient client, long chatId, int messageId, CancellationToken ct)
        => await client.EditMessageText(chatId, messageId, await BuildStatusTextAsync(ct),
            replyMarkup: BuildStatusKeyboard(), cancellationToken: ct);

    // ── /ping — пряме опитування всіх серверів у реальному часі ──────────────

    private async Task SendPingNowAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        if (!access.TryConsumePingCooldown(chatId, out var remaining))
        {
            await client.SendMessage(chatId,
                $"⏳ Зачекайте ще {Math.Ceiling(remaining.TotalSeconds)}с перед наступним пінгом.",
                cancellationToken: ct);
            return;
        }

        var placeholder = await client.SendMessage(chatId, "🏓 Пінгую всі сервери…", cancellationToken: ct);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<PingResult> results;
        try
        {
            results = await pingMonitor.PingAllNowAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TelegramBotService: помилка виконання /ping.");
            await client.EditMessageText(chatId, placeholder.MessageId,
                "❌ Помилка під час пінгування серверів.", cancellationToken: ct);
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

        string header = $"🏓 Результат пінгу ({results.Count} серв., {sw.Elapsed.TotalSeconds:F1}с)\n" +
                         $"✅ {online} online   🔴 {offline} offline\n\n";

        var pages = TelegramTextChunker.BuildPages(lines, header);
        _pagedScreens[chatId] = new TelegramPagedScreen("ping", pages);

        await client.EditMessageText(chatId, placeholder.MessageId, pages[0],
            replyMarkup: BuildPaginationKeyboard("ping", 0, pages.Count),
            cancellationToken: ct);
    }

    private static IEnumerable<string> BuildGroupLines(IGrouping<string, PingResult> group)
    {
        yield return $"— {group.Key} —";
        foreach (var r in group.OrderBy(x => x.Name))
        {
            string icon = r.Status switch
            {
                PingStatus.Online  => "✅",
                PingStatus.Offline => "🔴",
                _                  => "⏳"
            };
            string latency = r.Status == PingStatus.Online && r.LatencyMs is not null
                ? $" — {r.LatencyMs} мс"
                : string.Empty;

            yield return $"{icon} {r.Name} ({r.IP}){latency}";
        }
    }

    private async Task<string> BuildStatusTextAsync(CancellationToken ct)
    {
        var pingSnapshot = pingMonitor.GetSnapshot();
        int offline = pingSnapshot.Values.Count(s => s == PingStatus.Offline);
        int online  = pingSnapshot.Values.Count(s => s == PingStatus.Online);

        int openIncidents = uptimeTracker.GetSnapshot().Count(r => !r.IsResolved);

        // Аудит-фікс п.4/п.5: раніше рахувалось напряму з rdpMonitor.GetSnapshot()
        // без перевірки тумблера — /status показував останню відому (застарілу)
        // кількість RDP-сесій навіть після вимкнення RDP-моніторингу в Settings.
        // Той самий клас бага, що на Overview для Zabbix — тут же той самий
        // патерн перевірки, що вже є в SendRdpPickerAsync/SendBackupsListAsync.
        bool rdpEnabled = (await GetAppSettingsAsync(ct)).RdpMonitoringEnabled;
        string rdpLine = rdpEnabled
            ? $"🖥 RDP-сесій (активних): {rdpMonitor.GetSnapshot().Values.Sum(list => list.Count(s => s.State == RdpSessionState.Active))}"
            : "🖥 RDP-сесій: моніторинг вимкнено в Settings";

        int activeMaintenance = maintenance.GetActiveWindows().Count;

        return $"📊 Статус інфраструктури\n" +
               $"✅ Онлайн: {online}\n" +
               $"🔴 Офлайн: {offline}\n" +
               $"⏱ Відкритих інцидентів: {openIncidents}\n" +
               $"{rdpLine}\n" +
               $"🔧 Активних вікон обслуговування: {activeMaintenance}";
    }

    private static InlineKeyboardMarkup BuildStatusKeyboard() =>
        new(Array.Empty<InlineKeyboardButton[]>());

    private async Task SendRdpPickerAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        if (!(await GetAppSettingsAsync(ct)).RdpMonitoringEnabled)
        {
            await client.SendMessage(chatId, "🖥 Моніторинг RDP-сесій зараз вимкнено в Settings.\n", cancellationToken: ct);
            return;
        }

        if (IsSingleTerminalServer)
        {
            await SendRdpSessionsDirectAsync(client, chatId, _terminalServers[0].IP, ct);
            return;
        }

        await client.SendMessage(chatId, "Оберіть сервер:",
            replyMarkup: BuildRdpPickerKeyboard(), cancellationToken: ct);
    }

    private async Task SendRdpSessionsDirectAsync(
        ITelegramBotClient client, long chatId, string serverIp, CancellationToken ct)
    {
        var snapshot = rdpMonitor.GetSnapshot();
        var sessions = snapshot.TryGetValue(serverIp, out var list) ? list : [];

        var lines = sessions.Count == 0
            ? new List<string> { "Немає активних сесій." }
            : sessions.Select(s => $"{s.Username} — {s.State} (logon: {s.LogonTime})").ToList();

        var pages = TelegramTextChunker.BuildPages(lines, header: "🖥 Сесії:\n");
        _pagedScreens[chatId] = new TelegramPagedScreen("rdp_direct", pages);

        await client.SendMessage(chatId, pages[0],
            replyMarkup: BuildPaginationKeyboard("rdp_direct", 0, pages.Count),
            cancellationToken: ct);
    }

    private async Task EditWithRdpPickerAsync(ITelegramBotClient client, long chatId, int messageId, CancellationToken ct)
    {
        if (!(await GetAppSettingsAsync(ct)).RdpMonitoringEnabled)
        {
            await client.EditMessageText(chatId, messageId,
                "🖥 Моніторинг RDP-сесій зараз вимкнено в Settings.", cancellationToken: ct);
            return;
        }

        await client.EditMessageText(chatId, messageId, "Оберіть сервер:",
            replyMarkup: BuildRdpPickerKeyboard(), cancellationToken: ct);
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
                "🖥 Моніторинг RDP-сесій зараз вимкнено в Settings.", cancellationToken: ct);
            return;
        }

        var snapshot = rdpMonitor.GetSnapshot();
        var sessions = snapshot.TryGetValue(serverIp, out var list) ? list : [];

        var lines = sessions.Count == 0
            ? new List<string> { "Немає активних сесій." }
            : sessions.Select(s => $"{s.Username} — {s.State} (logon: {s.LogonTime})").ToList();

        var pages = TelegramTextChunker.BuildPages(lines, header: "🖥 Сесії:\n");

        var keyboard = IsSingleTerminalServer
            ? null
            : new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("◀ Назад", "back:rdp_picker") }
            });

        await client.EditMessageText(chatId, messageId, pages[0], replyMarkup: keyboard, cancellationToken: ct);
    }

    private async Task SendUsersListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        var users = access.GetAllowedUsers();

        if (users.Count == 0)
        {
            await client.SendMessage(chatId, "👥 Дозволених користувачів немає.", cancellationToken: ct);
            return;
        }

        var rows = users
            .Select(u => new[] { InlineKeyboardButton.WithCallbackData(
                $"🚫 @{u.Username} ({u.ChatId})", $"revoke:{u.ChatId}") })
            .ToArray();

        await client.SendMessage(chatId, "👥 Дозволені користувачі:",
            replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    // ── Офлайн / Інциденти / Обслуговування (з пагінацією) ──────────────────

    private async Task SendOfflineListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        var snapshot = pingMonitor.GetSnapshot();

        var lines = _allServers
            .Where(s => snapshot.TryGetValue(s.IP, out var status) && status == PingStatus.Offline)
            .Select(s => $"🔴 {s.Name} ({s.IP}) — {s.Group}")
            .ToList();

        if (lines.Count == 0) lines.Add("Немає офлайн-серверів. ✅");

        await SendPagedScreenAsync(client, chatId, "offline", "🔴 Офлайн-сервери:\n", lines, ct);
    }

    private async Task SendIncidentsListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        var openIncidents = uptimeTracker.GetSnapshot()
            .Where(r => !r.IsResolved)
            .OrderByDescending(r => r.FellAt)
            .ToList();

        var lines = openIncidents
            .Select(r => $"⏱ {r.ServerName} ({r.ServerIp})\n   Впав: {r.FellAt:dd.MM HH:mm} — триває {r.DurationDisplay}")
            .ToList();

        if (lines.Count == 0) lines.Add("Відкритих інцидентів немає. ✅");

        await SendPagedScreenAsync(client, chatId, "incidents", "⏱ Відкриті інциденти:\n", lines, ct);
    }

    private async Task SendMaintenanceListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        var windows = maintenance.GetActiveWindows()
            .OrderBy(w => w.To ?? DateTimeOffset.MaxValue)
            .ToList();

        var lines = windows
            .Select(w => $"- {w.DisplayName}\n" +
                         $"   {(string.IsNullOrWhiteSpace(w.Reason) ? "Без причини" : w.Reason)}\n" +
                         $"   {(w.To is { } to ? $"До {to.ToLocalTime():dd.MM HH:mm}." : "без обмеження часу.")}")
            .ToList();

        if (lines.Count == 0) lines.Add("Активних вікон обслуговування немає.");

        await SendPagedScreenAsync(client, chatId, "maintenance", "🔧 Обслуговування:\n", lines, ct);
    }

    /// <summary>
    /// Backup Verification: живий знімок напряму з IBackupStateRepository
    /// (не GetSnapshot() — BackupMonitorJob, Hangfire, не тримає стан між
    /// запусками; дані ті самі, просто джерело — БД, а не in-memory кеш).
    /// </summary>
    private async Task SendBackupsListAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        if (!(await GetAppSettingsAsync(ct)).BackupMonitoringEnabled)
        {
            await client.SendMessage(chatId,
                "💾 Backup-моніторинг зараз вимкнено в Settings.\nДані про стан бекапів не оновлюються.",
                cancellationToken: ct);
            return;
        }

        var states = (await GetBackupSnapshotAsync(ct))
            .OrderBy(s => s.Host)
            .ThenBy(s => s.Name)
            .ThenBy(s => s.Kind)
            .ToList();

        var lines = states
            .Select(s =>
            {
                bool underMaintenance =
                    _serverLookup.TryGetValue(s.Host, out var entry) &&
                    maintenance.IsUnderMaintenance(entry.IP, entry.Group);

                string maintenanceBadge = underMaintenance ? " 🔧" : string.Empty;
                string maintenanceText  = underMaintenance ? "\n            [Maintenance]" : string.Empty;

                string statusDisplay = s.Outcome == BackupOutcome.Ok
                    ? (s.LastConfirmedAt is { } at ? at.ToLocalTime().ToString("dd.MM HH:mm") : "Невідомо")
                    : s.Outcome.ToString().ToUpper();

                string kindTag = s.Kind == BackupKind.Diff ? " [Diff]" : string.Empty;

                return $"{BackupIcon(s.Outcome)}{maintenanceBadge} {s.Name}{kindTag} — {statusDisplay}{maintenanceText}";
            })
            .ToList();

        if (lines.Count == 0)
            lines.Add("Перевірки бекапів не сконфігуровано (BackupChecks у appsettings.json).");

        await SendPagedScreenAsync(client, chatId, "backups", "💾 Статус бекапів:\n", lines, ct);
    }

    private static string BackupIcon(BackupOutcome outcome) => outcome switch
    {
        BackupOutcome.Ok          => "✅",
        BackupOutcome.SizeWarning => "⚠️",
        BackupOutcome.Stale       => "⏰",
        BackupOutcome.Missing     => "🚫",
        _                         => "❓"
    };

    private async Task SendPagedScreenAsync(
        ITelegramBotClient client, long chatId, string screenKey, string header,
        List<string> lines, CancellationToken ct)
    {
        var pages = TelegramTextChunker.BuildPages(lines, header);
        _pagedScreens[chatId] = new TelegramPagedScreen(screenKey, pages);

        await client.SendMessage(chatId, pages[0],
            replyMarkup: BuildPaginationKeyboard(screenKey, 0, pages.Count),
            cancellationToken: ct);
    }

    private static InlineKeyboardMarkup BuildPaginationKeyboard(string screenKey, int pageIndex, int pageCount)
    {
        if (pageCount <= 1)
            return new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>());

        var row = new List<InlineKeyboardButton>();

        if (pageIndex > 0)
            row.Add(InlineKeyboardButton.WithCallbackData("◀ Назад", $"page:{screenKey}:{pageIndex - 1}"));

        if (pageIndex < pageCount - 1)
            row.Add(InlineKeyboardButton.WithCallbackData("Далі ▶", $"page:{screenKey}:{pageIndex + 1}"));

        return new InlineKeyboardMarkup(new[] { row.ToArray() });
    }

    // ── Scoped-доступ до Phase 2 репозиторіїв (Singleton → Scoped) ──────────

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
