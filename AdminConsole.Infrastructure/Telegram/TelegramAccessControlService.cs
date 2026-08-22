using System.Collections.Concurrent;
using System.Security.Cryptography;
using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Infrastructure.Telegram;

/// <summary>Пара chat_id + username для відображення в списку дозволених користувачів.</summary>
public sealed record TelegramAllowedUserView(long ChatId, string Username);

/// <summary>
/// Централізована перевірка доступу до Telegram-бота: Primary Admin,
/// approval-флоу для інших користувачів, rate limiting.
///
/// T5.3: UserSettingsService (файлова персистентність WPF) → IAppSettingsRepository
/// (EF Core, Фаза 2), той самий IServiceScopeFactory-патерн, що решта
/// Singleton-сервісів Фази 4/5 (репозиторій Scoped, сервіс Singleton).
/// PrimaryAdminChatId/AllowedUsers кешуються в пам'яті (як і раніше — гарячий
/// шлях: IsAllowed/IsPrimaryAdmin викликаються на КОЖНЕ вхідне повідомлення
/// бота), явно завантажуються один раз через InitializeAsync (викликає
/// TelegramBotService.ExecuteAsync до старту polling), і синхронно
/// пишуться в кеш + асинхронно в БД на кожній мутації (Approve/Deny/
/// Revoke/TryClaimAdmin/RefreshUsername).
///
/// Pending-запити, claim-код, rate-limit — і далі ЛИШЕ в пам'яті, без змін:
/// це навмисно ефемерний стан (як і в оригіналі), що не переживає рестарт.
/// </summary>
public sealed class TelegramAccessControlService(
    IMediator                             mediator,
    IServiceScopeFactory                  scopeFactory,
    ILogger<TelegramAccessControlService> logger)
{
    private const string LogSource = "TelegramAccess";

    // ── Кеш персистентного стану (PrimaryAdmin + AllowedUsers) ─────────────

    private long? _primaryAdminChatId;
    private readonly Dictionary<long, string?> _allowedUsers = new();
    private readonly object _stateLock = new();

    /// <summary>
    /// Викликається ОДИН раз з TelegramBotService.ExecuteAsync до старту
    /// long-polling — той самий принцип, що LoadFromDbAsync у MaintenanceService/
    /// UptimeTrackerService (Фаза 4): гарантія, що кеш заповнений ДО того,
    /// як перше вхідне повідомлення від Telegram могло б його прочитати.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();

        var settings = await repo.GetAsync(ct);
        var users    = await repo.GetTelegramAllowedUsersAsync(ct);

        lock (_stateLock)
        {
            _primaryAdminChatId = settings.TelegramPrimaryAdminChatId;
            _allowedUsers.Clear();
            foreach (var u in users)
                _allowedUsers[u.ChatId] = u.Username;
        }

        logger.LogInformation(
            "TelegramAccessControlService: завантажено PrimaryAdmin={PrimaryAdmin}, {Count} дозволених користувач(ів).",
            _primaryAdminChatId, users.Count);
    }

    private async Task<T> WithAppSettingsAsync<T>(Func<IAppSettingsRepository, Task<T>> action)
    {
        using var scope = scopeFactory.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>());
    }

    private Task WithAppSettingsAsync(Func<IAppSettingsRepository, Task> action) =>
        WithAppSettingsAsync(async r => { await action(r); return true; });

    // ── Pending requests ────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<int, Domain.Models.TelegramPendingRequest> _pending = new();
    private int _nextPendingId;

    private static readonly TimeSpan PendingRequestTtl = TimeSpan.FromHours(24);
    private const int MaxPendingRequests = 50;

    private readonly ConcurrentDictionary<long, DateTimeOffset> _requestCooldownUntil = new();
    private static readonly TimeSpan RequestCooldown = TimeSpan.FromMinutes(15);

    // ── Claim-код для Primary Admin ─────────────────────────────────────────

    private string?         _claimCode;
    private DateTimeOffset  _claimCodeExpiresAt;
    private readonly object _claimLock = new();

    // ── Rate limiting ────────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<long, ConcurrentQueue<DateTimeOffset>> _rateLimits = new();
    private const int RateLimitMaxActions = 10;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<long, DateTimeOffset> _pingCooldownUntil = new();
    private static readonly TimeSpan PingCooldown = TimeSpan.FromSeconds(20);

    public readonly record struct PendingRequestResult(
        Domain.Models.TelegramPendingRequest? Request,
        TimeSpan?                              CooldownRemaining);

    // ── Primary Admin claim-флоу ─────────────────────────────────────────────

    public bool IsPrimaryAdminClaimed { get { lock (_stateLock) return _primaryAdminChatId is not null; } }

    public bool IsPrimaryAdmin(long chatId) { lock (_stateLock) return _primaryAdminChatId == chatId; }

    public long? PrimaryAdminChatId { get { lock (_stateLock) return _primaryAdminChatId; } }

    /// <summary>Генерує 6-значний код, дійсний 10 хв. Лише в пам'яті — не переживає перезапуск (навмисно, безпечніше).</summary>
    public (string Code, DateTimeOffset ExpiresAt) GenerateClaimCode()
    {
        lock (_claimLock)
        {
            _claimCode          = RandomNumberGenerator.GetInt32(100_000, 999_999).ToString();
            _claimCodeExpiresAt = DateTimeOffset.Now.AddMinutes(10);
            return (_claimCode, _claimCodeExpiresAt);
        }
    }

    public async Task<bool> TryClaimAdminAsync(string code, long chatId, CancellationToken ct = default)
    {
        if (IsPrimaryAdminClaimed) return false;

        lock (_claimLock)
        {
            if (_claimCode is null || DateTimeOffset.Now > _claimCodeExpiresAt)
                return false;

            if (!string.Equals(_claimCode, code.Trim(), StringComparison.Ordinal))
                return false;

            _claimCode = null; // одноразовий
        }

        // Аудит Зона 2 (2026-08-22): точкове оновлення лише одного поля —
        // не GetAsync+SaveAsync повного об'єкта (lost update із паралельним
        // записом RdpMonitorService/MonitoringController).
        await WithAppSettingsAsync(r => r.UpdateTelegramPrimaryAdminAsync(chatId, ct));

        lock (_stateLock) _primaryAdminChatId = chatId;

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Telegram Primary Admin прив'язано: chat_id={chatId}."), ct);

        return true;
    }

    // ── Доступ read-only користувачів ────────────────────────────────────────

    public bool IsAllowed(long chatId)
    {
        lock (_stateLock) return chatId == _primaryAdminChatId || _allowedUsers.ContainsKey(chatId);
    }

    public IReadOnlyList<long> GetAllowedChatIds()
    {
        lock (_stateLock) return _allowedUsers.Keys.ToList();
    }

    /// <summary>Список дозволених користувачів разом з username, для показу в боті ("👥 Користувачі").</summary>
    public IReadOnlyList<TelegramAllowedUserView> GetAllowedUsers()
    {
        lock (_stateLock)
            return _allowedUsers
                .Select(kv => new TelegramAllowedUserView(kv.Key, kv.Value ?? "невідомо"))
                .ToList();
    }

    /// <summary>
    /// Додає користувача напряму (Settings UI, T6.2 п.3) — без проходження
    /// approval-флоу через pending request. Той самий ефект, що й
    /// ApproveAsync, просто ініційований адміном, а не вхідним /start.
    /// Idempotent: повторний виклик для вже дозволеного chat_id лише
    /// оновлює username.
    /// </summary>
    public async Task AddAllowedUserAsync(long chatId, string? username, CancellationToken ct = default)
    {
        lock (_stateLock) _allowedUsers[chatId] = username;

        await WithAppSettingsAsync(r => r.UpsertTelegramAllowedUserAsync(chatId, username, ct));

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Telegram доступ додано вручну через Settings: chat_id={chatId}" +
            (username is null ? "." : $", @{username}.")), ct);
        await mediator.Publish(new TelegramAccessChangedOccurred(
            TelegramAccessAction.Approved, chatId, username), ct);
    }

    public async Task<bool> RevokeAsync(long chatId, CancellationToken ct = default)
    {
        bool removed;
        lock (_stateLock) removed = _allowedUsers.Remove(chatId);
        if (!removed) return false;

        await WithAppSettingsAsync(r => r.RemoveTelegramAllowedUserAsync(chatId, ct));
        _requestCooldownUntil[chatId] = DateTimeOffset.Now.Add(RequestCooldown);

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Telegram доступ відкликано: chat_id={chatId}. " +
            $"Кулдаун на повторний запит: {RequestCooldown.TotalMinutes} хв."), ct);
        await mediator.Publish(new TelegramAccessChangedOccurred(TelegramAccessAction.Revoked, chatId, null), ct);
        return true;
    }

    // ── Pending requests (approval-флоу) ────────────────────────────────────

    public async Task<PendingRequestResult> RegisterPendingRequestAsync(
        long chatId, string username, CancellationToken ct = default)
    {
        var existing = _pending.Values.FirstOrDefault(p => p.ChatId == chatId);
        if (existing is not null) return new PendingRequestResult(existing, null);

        if (_requestCooldownUntil.TryGetValue(chatId, out var until))
        {
            var remaining = until - DateTimeOffset.Now;
            if (remaining > TimeSpan.Zero)
                return new PendingRequestResult(null, remaining);

            _requestCooldownUntil.TryRemove(chatId, out _);
        }

        PurgeExpiredPending();
        PurgeExpiredThrottleState();

        if (_pending.Count >= MaxPendingRequests)
        {
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"Досягнуто ліміту pending-запитів ({MaxPendingRequests}). " +
                $"Новий запит від chat_id={chatId} відхилено без реєстрації."), ct);
            return new PendingRequestResult(null, null);
        }

        int id = Interlocked.Increment(ref _nextPendingId);
        var request = new Domain.Models.TelegramPendingRequest(id, chatId, username, DateTimeOffset.Now);
        _pending[id] = request;

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Новий запит доступу: @{username} (chat_id={chatId})."), ct);
        await mediator.Publish(new TelegramAccessRequestOccurred(request), ct);

        return new PendingRequestResult(request, null);
    }

    private void PurgeExpiredPending()
    {
        var cutoff = DateTimeOffset.Now - PendingRequestTtl;
        foreach (var (id, request) in _pending)
            if (request.RequestedAt < cutoff)
                _pending.TryRemove(id, out _);
    }

    private void PurgeExpiredThrottleState()
    {
        var now = DateTimeOffset.Now;

        foreach (var (chatId, until) in _requestCooldownUntil)
            if (until <= now)
                _requestCooldownUntil.TryRemove(chatId, out _);

        foreach (var (chatId, until) in _pingCooldownUntil)
            if (until <= now)
                _pingCooldownUntil.TryRemove(chatId, out _);

        foreach (var (chatId, queue) in _rateLimits)
        {
            if (queue.IsEmpty ||
                (queue.TryPeek(out var oldest) && now - oldest > RateLimitWindow))
            {
                _rateLimits.TryRemove(chatId, out _);
            }
        }
    }

    public Domain.Models.TelegramPendingRequest? TryGetPending(int id) =>
        _pending.TryGetValue(id, out var r) ? r : null;

    public IReadOnlyList<Domain.Models.TelegramPendingRequest> GetAllPending()
    {
        PurgeExpiredPending();
        return _pending.Values.ToList();
    }

    public async Task<bool> ApproveAsync(int id, CancellationToken ct = default)
    {
        if (!_pending.TryRemove(id, out var request)) return false;

        lock (_stateLock) _allowedUsers[request.ChatId] = request.Username;
        await WithAppSettingsAsync(r => r.UpsertTelegramAllowedUserAsync(request.ChatId, request.Username, ct));

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Доступ дозволено: @{request.Username} (chat_id={request.ChatId})."), ct);
        await mediator.Publish(new TelegramAccessChangedOccurred(
            TelegramAccessAction.Approved, request.ChatId, request.Username), ct);
        return true;
    }

    public async Task<bool> DenyAsync(int id, CancellationToken ct = default)
    {
        if (!_pending.TryRemove(id, out var request)) return false;

        _requestCooldownUntil[request.ChatId] = DateTimeOffset.Now.Add(RequestCooldown);

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Доступ відхилено: @{request.Username} (chat_id={request.ChatId}). " +
            $"Кулдаун на повторний запит: {RequestCooldown.TotalMinutes} хв."), ct);
        await mediator.Publish(new TelegramAccessChangedOccurred(
            TelegramAccessAction.Denied, request.ChatId, request.Username), ct);
        return true;
    }

    // ── Rate limiting ────────────────────────────────────────────────────────

    public bool CheckRateLimit(long chatId)
    {
        var queue = _rateLimits.GetOrAdd(chatId, _ => new ConcurrentQueue<DateTimeOffset>());
        var now   = DateTimeOffset.Now;

        while (queue.TryPeek(out var oldest) && now - oldest > RateLimitWindow)
            queue.TryDequeue(out _);

        if (queue.Count >= RateLimitMaxActions)
            return false;

        queue.Enqueue(now);
        return true;
    }

    public bool TryConsumePingCooldown(long chatId, out TimeSpan remaining)
    {
        var now = DateTimeOffset.Now;

        if (_pingCooldownUntil.TryGetValue(chatId, out var until) && until > now)
        {
            remaining = until - now;
            return false;
        }

        _pingCooldownUntil[chatId] = now.Add(PingCooldown);
        remaining = TimeSpan.Zero;
        return true;
    }

    /// <summary>
    /// Оновлює збережений username для вже дозволеного (не Primary Admin)
    /// chat_id — викликається при кожному /start. Primary Admin навмисно
    /// не потрапляє у TelegramAllowedUsers (і не показується в GetAllowedUsers) —
    /// той самий принцип, що й у старому WPF.
    /// </summary>
    public async Task RefreshUsernameAsync(long chatId, string username, CancellationToken ct = default)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = _allowedUsers.ContainsKey(chatId) && _allowedUsers[chatId] != username;
            if (changed) _allowedUsers[chatId] = username;
        }

        if (!changed) return;
        await WithAppSettingsAsync(r => r.UpsertTelegramAllowedUserAsync(chatId, username, ct));
    }
}
