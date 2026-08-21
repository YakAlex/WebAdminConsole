namespace AdminConsole.Domain.Models;

/// <summary>
/// Персистентний секрет (Фаза 5, T5.1) — заміна Win32 Credential Manager.
/// Target — "Rdp" / "Zabbix" / "Telegram" (одна активна пара на ціль,
/// той самий принцип, що RdpTarget/ZabbixTarget/TelegramTarget у старому
/// CredentialStore). Username лишається у відкритому вигляді (не є
/// секретом сам по собі — DOMAIN\\user або порожній для Zabbix API-токена);
/// ProtectedSecret — пароль/токен, зашифрований через IDataProtector
/// (ключі — DPAPI-NG, T3.3), ніколи не зберігається у відкритому вигляді.
/// </summary>
public sealed class StoredCredential
{
    public string  Target          { get; set; } = string.Empty;
    public string? Username        { get; set; }
    public byte[]  ProtectedSecret { get; set; } = [];
}
