using MediatR;

namespace AdminConsole.Domain.Events;

public enum CredentialTarget { Zabbix, Telegram }
public enum CredentialAction { Saved, Cleared }

/// <summary>
/// Публікується після збереження/очищення credentials через Settings.
/// ZabbixPollerService підписується і негайно переривають Task.Delay щоб
/// застосувати нові credentials без очікування. Сервіс реагує тільки на
/// Action = Saved — при Cleared нічого не робить.
///
/// Rdp прибрано з CredentialTarget: бекенд-служба тепер працює під
/// виділеним доменним акаунтом (DOMAIN\svc_adminconsole), quser.exe
/// відпрацьовує через Kerberos у контексті процесу — RDP credentials
/// більше не існує як концепція.
///
/// Заміна CredentialsChangedMessage.
/// </summary>
public sealed record CredentialsChangedOccurred(
    CredentialTarget Target,
    CredentialAction Action
) : INotification;
