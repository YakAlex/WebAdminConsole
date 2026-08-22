using System.Security.Cryptography;
using System.Text;
using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace AdminConsole.Infrastructure.Security;

/// <summary>
/// T5.1: rewritten on top of the ASP.NET Core Data Protection API. The Win32
/// Credential Manager (CredWrite/CredRead/CredDelete) backing store has been
/// FULLY removed — passwords/tokens are stored in the StoredCredentials
/// table (SQLite, Phase 2), encrypted via IDataProtector. Encryption keys are
/// the same ones configured in Program.cs (T3.3): PersistKeysToFileSystem +
/// ProtectKeysWithDpapiNG().
///
/// RDP secrets have been removed entirely (architectural decision: the
/// backend service now runs under a dedicated domain account
/// DOMAIN\svc_adminconsole with rights on the target servers — quser.exe
/// runs in the context of the process itself via Kerberos, with no
/// CredWrite/CredRead and no separate RDP credentials at all). Only Zabbix
/// and Telegram remain.
///
/// The public contract (method signatures) stays as close to the original as
/// possible — only Load/Store/Clear became async (the repository requires
/// it); the rest (Has*/Get*/masked tokens) remain synchronous reads from the
/// in-memory cache, as before — a Pull read on the hot path
/// (ZabbixPollerService polls every cycle) shouldn't hit the DB every time.
/// </summary>
public sealed class CredentialStore
{
    private const string ZabbixTarget   = "Zabbix";
    private const string TelegramTarget = "Telegram";

    private const string ProtectionPurpose = "AdminConsole.CredentialStore.v1";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector       _protector;

    private string? _zabbixToken;
    private string? _zabbixUsername;
    private string? _telegramToken;
    private readonly object _lock = new();

    private volatile bool _userCancelledZabbixPrompt;

    public CredentialStore(IServiceScopeFactory scopeFactory, IDataProtectionProvider dataProtectionProvider)
    {
        _scopeFactory = scopeFactory;
        _protector    = dataProtectionProvider.CreateProtector(ProtectionPurpose);
    }

    public bool UserCancelledZabbixPrompt
    {
        get => _userCancelledZabbixPrompt;
        private set => _userCancelledZabbixPrompt = value;
    }

    // ── Zabbix ───────────────────────────────────────────────────────────────

    public bool HasZabbixCredentials
    {
        get { lock (_lock) return !string.IsNullOrWhiteSpace(_zabbixToken); }
    }

    public bool ZabbixUsesApiToken
    {
        get { lock (_lock) return string.IsNullOrWhiteSpace(_zabbixUsername) &&
                                  !string.IsNullOrWhiteSpace(_zabbixToken); }
    }

    public (string Username, string Token) GetZabbix()
    {
        lock (_lock) return (_zabbixUsername ?? string.Empty, _zabbixToken ?? string.Empty);
    }

    public async Task LoadZabbixFromStoreAsync(CancellationToken ct = default)
    {
        var cred = await WithRepositoryAsync(r => r.GetAsync(ZabbixTarget, ct));
        if (cred is null) return;

        lock (_lock)
        {
            _zabbixUsername = cred.Username;
            _zabbixToken    = Unprotect(cred.ProtectedSecret);
        }
    }

    public async Task StoreZabbixTokenAsync(string apiToken, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _zabbixUsername           = string.Empty;
            _zabbixToken              = apiToken;
            UserCancelledZabbixPrompt = false;
        }

        await WithRepositoryAsync(r => r.UpsertAsync(new StoredCredential
        {
            Target = ZabbixTarget, Username = string.Empty, ProtectedSecret = Protect(apiToken)
        }, ct));
    }

    public async Task StoreZabbixCredentialsAsync(string username, string password, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _zabbixUsername           = username;
            _zabbixToken              = password;
            UserCancelledZabbixPrompt = false;
        }

        await WithRepositoryAsync(r => r.UpsertAsync(new StoredCredential
        {
            Target = ZabbixTarget, Username = username, ProtectedSecret = Protect(password)
        }, ct));
    }

    public async Task ClearZabbixAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _zabbixUsername = null;
            _zabbixToken    = null;
            _userCancelledZabbixPrompt = false;
        }

        await WithRepositoryAsync(r => r.DeleteAsync(ZabbixTarget, ct));
    }

    public void MarkZabbixCancelled() => _userCancelledZabbixPrompt = true;

    /// <summary>Masked token for Settings: "••••••••ab3f".</summary>
    public string GetZabbixTokenMasked()
    {
        lock (_lock) return Mask(_zabbixToken);
    }

    public void ResetZabbixCancelledFlag() => _userCancelledZabbixPrompt = false;

    // ── Telegram ─────────────────────────────────────────────────────────────

    public bool HasTelegramCredentials
    {
        get { lock (_lock) return !string.IsNullOrWhiteSpace(_telegramToken); }
    }

    public string GetTelegramToken()
    {
        lock (_lock) return _telegramToken ?? string.Empty;
    }

    public async Task LoadTelegramFromStoreAsync(CancellationToken ct = default)
    {
        var cred = await WithRepositoryAsync(r => r.GetAsync(TelegramTarget, ct));
        if (cred is null) return;

        lock (_lock) _telegramToken = Unprotect(cred.ProtectedSecret);
    }

    public async Task StoreTelegramTokenAsync(string botToken, CancellationToken ct = default)
    {
        lock (_lock) _telegramToken = botToken;

        await WithRepositoryAsync(r => r.UpsertAsync(new StoredCredential
        {
            Target = TelegramTarget, Username = null, ProtectedSecret = Protect(botToken)
        }, ct));
    }

    public async Task ClearTelegramAsync(CancellationToken ct = default)
    {
        lock (_lock) _telegramToken = null;

        await WithRepositoryAsync(r => r.DeleteAsync(TelegramTarget, ct));
    }

    /// <summary>Masked token for display in Settings — same approach as Zabbix.</summary>
    public string GetTelegramTokenMasked()
    {
        lock (_lock) return Mask(_telegramToken);
    }

    // ── Data Protection ──────────────────────────────────────────────────────

    /// <summary>
    /// Audit Zone 4, Finding #2 (2026-08-22): unlike Unprotect() below (which
    /// has correctly degraded to "secret unavailable" on a broken/rotated key
    /// ring for years), writes previously had NO protection at all —
    /// CryptographicException flew uncaught all the way to a bare HTTP 500 with
    /// no explanation. The most likely real-world scenario is exactly the
    /// moment the admin tries to "fix" the situation by re-saving the token.
    /// </summary>
    private byte[] Protect(string plaintext)
    {
        try
        {
            return _protector.Protect(Encoding.UTF8.GetBytes(plaintext));
        }
        catch (CryptographicException ex)
        {
            throw new CredentialProtectionException(
                "Failed to encrypt the secret — the encryption keys (DPAPI-NG) are unavailable or " +
                "corrupted. The service may have been reconfigured to run under a different account, or the " +
                "encryption key folder was lost. Contact your administrator.", ex);
        }
    }

    /// <summary>
    /// Encryption key rotation/loss is an entirely realistic scenario (e.g.
    /// restoring the service on a new machine without migrating the key-ring
    /// folder) — treated as "secret unavailable", not as a fatal startup
    /// error: we return an empty string, HasXCredentials correctly becomes
    /// false, and the poller will ask the user to save credentials again via Settings.
    /// </summary>
    private string Unprotect(byte[] blob)
    {
        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(blob));
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
    }

    private static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return string.Empty;
        return secret.Length <= 4
            ? new string('•', secret.Length)
            : $"••••••••{secret[^4..]}";
    }

    // ── Scoped repository access (Singleton → Scoped, T2.3/Phase-4 pattern) ────

    private async Task<T> WithRepositoryAsync<T>(Func<ICredentialRepository, Task<T>> action)
    {
        using var scope = _scopeFactory.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ICredentialRepository>());
    }

    private Task WithRepositoryAsync(Func<ICredentialRepository, Task> action) =>
        WithRepositoryAsync(async r => { await action(r); return true; });
}
