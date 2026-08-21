using System.Security.Cryptography;
using System.Text;
using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace AdminConsole.Infrastructure.Security;

/// <summary>
/// T5.1: переписано під ASP.NET Core Data Protection API. Win32 Credential
/// Manager (CredWrite/CredRead/CredDelete) як backing store ПОВНІСТЮ
/// видалено — паролі/токени зберігаються в таблиці StoredCredentials
/// (SQLite, Фаза 2), зашифровані через IDataProtector. Ключі шифрування —
/// ті самі, що налаштовані в Program.cs (T3.3): PersistKeysToFileSystem +
/// ProtectKeysWithDpapiNG().
///
/// RDP-секрети прибрано повністю (архітектурне рішення: бекенд-служба
/// тепер працює під виділеним доменним акаунтом DOMAIN\svc_adminconsole з
/// правами на цільових серверах — quser.exe відпрацьовує в контексті
/// самого процесу через Kerberos, без CredWrite/CredRead і без окремих
/// RDP-облікових даних узагалі). Лишились лише Zabbix і Telegram.
///
/// Публічний контракт (сигнатури методів) лишається максимально близьким
/// до оригіналу — лише Load/Store/Clear стали async (репозиторій вимагає
/// цього), решта (Has*/Get*/масковані токени) лишаються синхронними
/// читаннями з in-memory кешу, як і раніше — Pull-читання на гарячому
/// шляху (ZabbixPollerService опитує щоцикл) не повинне бити в БД щоразу.
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

    /// <summary>Замаскований токен для Settings: "••••••••ab3f".</summary>
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

    /// <summary>Маскований токен для показу в Settings — той самий підхід, що й Zabbix.</summary>
    public string GetTelegramTokenMasked()
    {
        lock (_lock) return Mask(_telegramToken);
    }

    // ── Data Protection ──────────────────────────────────────────────────────

    private byte[] Protect(string plaintext) => _protector.Protect(Encoding.UTF8.GetBytes(plaintext));

    /// <summary>
    /// Ротація/втрата ключів шифрування — цілком реальний сценарій (напр.
    /// відновлення сервісу на новій машині без перенесеної key-ring теки) —
    /// трактується як "секрет недоступний", а не як фатальна помилка старту:
    /// повертаємо порожній рядок, HasXCredentials коректно стане false,
    /// поллер попросить користувача зберегти credentials знову через Settings.
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

    // ── Scoped repository access (Singleton → Scoped, T2.3/Фаза4-патерн) ────

    private async Task<T> WithRepositoryAsync<T>(Func<ICredentialRepository, Task<T>> action)
    {
        using var scope = _scopeFactory.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ICredentialRepository>());
    }

    private Task WithRepositoryAsync(Func<ICredentialRepository, Task> action) =>
        WithRepositoryAsync(async r => { await action(r); return true; });
}
