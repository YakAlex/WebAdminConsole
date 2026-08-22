using MediatR;

namespace AdminConsole.Domain.Events;

public enum CredentialTarget { Zabbix, Telegram }
public enum CredentialAction { Saved, Cleared }

/// <summary>
/// Published after credentials are saved/cleared via Settings.
/// ZabbixPollerService subscribes and immediately breaks out of its
/// Task.Delay to apply the new credentials without waiting. The service
/// only reacts to Action = Saved — Cleared is a no-op for it.
///
/// Rdp was removed from CredentialTarget: the backend service now runs
/// under a dedicated domain account (DOMAIN\svc_adminconsole), and
/// quser.exe authenticates via Kerberos in the process context — RDP
/// credentials no longer exist as a concept.
///
/// Replaces CredentialsChangedMessage.
/// </summary>
public sealed record CredentialsChangedOccurred(
    CredentialTarget Target,
    CredentialAction Action
) : INotification;
