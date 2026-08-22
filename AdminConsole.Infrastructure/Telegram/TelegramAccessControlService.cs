using System.Collections.Concurrent;
using System.Security.Cryptography;
using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Infrastructure.Telegram;

/// <summary>Chat_id + username pair for display in the allowed-users list.</summary>
public sealed record TelegramAllowedUserView(long ChatId, string Username);

/// <summary>
/// Centralized access control for the Telegram bot: Primary Admin,
/// approval flow for other users, rate limiting.
///
/// T5.3: UserSettingsService (WPF file-based persistence) → IAppSettingsRepository
/// (EF Core, Phase 2), the same IServiceScopeFactory pattern used by the rest
/// of the Phase 4/5 Singleton services (Scoped repository, Singleton service).
/// PrimaryAdminChatId/AllowedUsers are cached in memory (as before — this is a
/// hot path: IsAllowed/IsPrimaryAdmin are called on EVERY incoming bot
/// message), loaded explicitly once via InitializeAsync (called by
/// TelegramBotService.ExecuteAsync before polling starts), and written
/// synchronously to the cache + asynchronously to the DB on every mutation
/// (Approve/Deny/Revoke/TryClaimAdmin/RefreshUsername).
///
/// Pending requests, claim code, rate limiting — remain IN-MEMORY ONLY, unchanged:
/// this is deliberately ephemeral state (as in the original) that doesn't survive a restart.
/// </summary>
public sealed class TelegramAccessControlService(
    IMediator                             mediator,
    IServiceScopeFactory                  scopeFactory,
    ILogger<TelegramAccessControlService> logger)
{
    private const string LogSource = "TelegramAccess";

    // ── Cache of persistent state (PrimaryAdmin + AllowedUsers) ─────────────

    private long? _primaryAdminChatId;
    private readonly Dictionary<long, string?> _allowedUsers = new();
    private readonly object _stateLock = new();

    /// <summary>
    /// Called ONCE from TelegramBotService.ExecuteAsync before long-polling
    /// starts — the same principle as LoadFromDbAsync in MaintenanceService/
    /// UptimeTrackerService (Phase 4): guarantees the cache is populated BEFORE
    /// the first incoming Telegram message could possibly read it.
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
            "TelegramAccessControlService: loaded PrimaryAdmin={PrimaryAdmin}, {Count} allowed user(s).",
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

    // ── Claim code for Primary Admin ─────────────────────────────────────────

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

    // ── Primary Admin claim flow ─────────────────────────────────────────────

    public bool IsPrimaryAdminClaimed { get { lock (_stateLock) return _primaryAdminChatId is not null; } }

    public bool IsPrimaryAdmin(long chatId) { lock (_stateLock) return _primaryAdminChatId == chatId; }

    public long? PrimaryAdminChatId { get { lock (_stateLock) return _primaryAdminChatId; } }

    /// <summary>Generates a 6-digit code, valid for 10 min. In-memory only — doesn't survive a restart (deliberately, for safety).</summary>
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

            _claimCode = null; // single-use
        }

        // Audit Zone 2 (2026-08-22): a targeted update of just this one field —
        // not GetAsync+SaveAsync of the whole object (lost update from a concurrent
        // write by RdpMonitorService/MonitoringController).
        await WithAppSettingsAsync(r => r.UpdateTelegramPrimaryAdminAsync(chatId, ct));

        lock (_stateLock) _primaryAdminChatId = chatId;

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Telegram Primary Admin claimed: chat_id={chatId}."), ct);

        return true;
    }

    // ── Read-only user access ────────────────────────────────────────

    public bool IsAllowed(long chatId)
    {
        lock (_stateLock) return chatId == _primaryAdminChatId || _allowedUsers.ContainsKey(chatId);
    }

    public IReadOnlyList<long> GetAllowedChatIds()
    {
        lock (_stateLock) return _allowedUsers.Keys.ToList();
    }

    /// <summary>List of allowed users together with their username, for display in the bot ("👥 Users").</summary>
    public IReadOnlyList<TelegramAllowedUserView> GetAllowedUsers()
    {
        lock (_stateLock)
            return _allowedUsers
                .Select(kv => new TelegramAllowedUserView(kv.Key, kv.Value ?? "unknown"))
                .ToList();
    }

    /// <summary>
    /// Adds a user directly (Settings UI, T6.2 item 3) — without going through the
    /// approval flow via a pending request. Same effect as
    /// ApproveAsync, just initiated by the admin instead of an incoming /start.
    /// Idempotent: a repeat call for an already-allowed chat_id just
    /// updates the username.
    /// </summary>
    public async Task AddAllowedUserAsync(long chatId, string? username, CancellationToken ct = default)
    {
        lock (_stateLock) _allowedUsers[chatId] = username;

        await WithAppSettingsAsync(r => r.UpsertTelegramAllowedUserAsync(chatId, username, ct));

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Telegram access added manually via Settings: chat_id={chatId}" +
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
            $"Telegram access revoked: chat_id={chatId}. " +
            $"Cooldown before another request: {RequestCooldown.TotalMinutes} min."), ct);
        await mediator.Publish(new TelegramAccessChangedOccurred(TelegramAccessAction.Revoked, chatId, null), ct);
        return true;
    }

    // ── Pending requests (approval flow) ────────────────────────────────────

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
                $"Pending-request limit reached ({MaxPendingRequests}). " +
                $"New request from chat_id={chatId} rejected without registering."), ct);
            return new PendingRequestResult(null, null);
        }

        int id = Interlocked.Increment(ref _nextPendingId);
        var request = new Domain.Models.TelegramPendingRequest(id, chatId, username, DateTimeOffset.Now);
        _pending[id] = request;

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"New access request: @{username} (chat_id={chatId})."), ct);
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
            $"Access approved: @{request.Username} (chat_id={request.ChatId})."), ct);
        await mediator.Publish(new TelegramAccessChangedOccurred(
            TelegramAccessAction.Approved, request.ChatId, request.Username), ct);
        return true;
    }

    public async Task<bool> DenyAsync(int id, CancellationToken ct = default)
    {
        if (!_pending.TryRemove(id, out var request)) return false;

        _requestCooldownUntil[request.ChatId] = DateTimeOffset.Now.Add(RequestCooldown);

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Access denied: @{request.Username} (chat_id={request.ChatId}). " +
            $"Cooldown before another request: {RequestCooldown.TotalMinutes} min."), ct);
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
    /// Updates the stored username for an already-allowed (non-Primary-Admin)
    /// chat_id — called on every /start. The Primary Admin deliberately
    /// never ends up in TelegramAllowedUsers (and isn't shown in GetAllowedUsers) —
    /// same principle as in the old WPF app.
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
