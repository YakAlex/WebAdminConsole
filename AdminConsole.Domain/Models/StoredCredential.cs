namespace AdminConsole.Domain.Models;

/// <summary>
/// Persistent secret (Phase 5, T5.1) — replaces the Win32 Credential Manager.
/// Target is "Rdp" / "Zabbix" / "Telegram" (one active pair per target,
/// the same principle as RdpTarget/ZabbixTarget/TelegramTarget in the old
/// CredentialStore). Username is stored in plain text (not a secret in
/// itself — DOMAIN\\user, or empty for a Zabbix API token);
/// ProtectedSecret is the password/token, encrypted via IDataProtector
/// (keys — DPAPI-NG, T3.3), and is never stored in plain text.
/// </summary>
public sealed class StoredCredential
{
    public string  Target          { get; set; } = string.Empty;
    public string? Username        { get; set; }
    public byte[]  ProtectedSecret { get; set; } = [];
}
