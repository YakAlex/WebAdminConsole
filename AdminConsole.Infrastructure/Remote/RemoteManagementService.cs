using System.Diagnostics;
using System.Management;
using AdminConsole.Domain.Events;
using MediatR;

namespace AdminConsole.Infrastructure.Remote;

/// <summary>
/// Executes remote management actions against servers.
/// All methods are async and run process/WMI work on the thread pool.
/// Results (success or error) are published via AppLogEntryOccurred so
/// they appear in the Logs feed automatically.
///
/// WMI dependency: System.Management (NuGet package on net8.0-windows).
///
/// T4.9: on-demand сервіс, НЕ BackgroundService — дії ініціюються кліком
/// користувача (майбутній API-контролер, Фаза 6), не циклом опитування.
/// </summary>
public sealed class RemoteManagementService(IMediator mediator)
{
    private const string LogSource = "RemoteMgmt";

    // ── Ping -t in a new terminal window ─────────────────────────────────────

    /// <summary>
    /// Opens a new cmd.exe window running "ping -t <ip>".
    /// Fire-and-forget — the window is independent of the app.
    /// </summary>
    public async Task OpenContinuousPingAsync(string ip, string serverName, CancellationToken ct = default)
    {
        if (!IsValidHostOrIp(ip))
        {
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Недійсний формат адреси для {serverName}: '{ip}'. Ping не запущено."), ct);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/k ping -t {ip}",
                UseShellExecute = true,
                CreateNoWindow  = false
            });

            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                $"Opened continuous ping window for {serverName} ({ip})."), ct);
        }
        catch (Exception ex)
        {
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Failed to open ping window for {serverName} ({ip}): {ex.Message}"), ct);
        }
    }

    // ── RDP connection ────────────────────────────────────────────────────────

    /// <summary>
    /// Launches mstsc.exe targeting the given IP.
    /// Async because Process.Start can briefly block on some systems
    /// when resolving the executable path.
    /// </summary>
    public async Task OpenRdpAsync(string ip, string serverName, CancellationToken ct = default)
    {
        try
        {
            await Task.Run(() => Process.Start(new ProcessStartInfo
            {
                FileName        = "mstsc.exe",
                Arguments       = $"/v:{ip}",
                UseShellExecute = true
            }), ct);

            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                $"Launched RDP session to {serverName} ({ip})."), ct);
        }
        catch (Exception ex)
        {
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Failed to launch RDP to {serverName} ({ip}): {ex.Message}"), ct);
        }
    }

    // ── Remote restart ────────────────────────────────────────────────────────

    /// <summary>
    /// Issues a WMI Win32_OperatingSystem.Reboot() call against the remote host.
    /// Requires the current user to have admin rights on the target machine.
    /// </summary>
    public async Task RemoteRestartAsync(string ip, string serverName, CancellationToken ct = default)
    {
        try
        {
            await Task.Run(() => ExecuteWmiShutdown(ip, isReboot: true), ct);

            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"RESTART command sent to {serverName} ({ip})."), ct);
        }
        catch (Exception ex)
        {
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Restart of {serverName} ({ip}) FAILED: {ex.Message}"), ct);
        }
    }

    // ── Remote shutdown ───────────────────────────────────────────────────────

    /// <summary>
    /// Issues a WMI Win32_OperatingSystem.Shutdown() call against the remote host.
    /// </summary>
    public async Task RemoteShutdownAsync(string ip, string serverName, CancellationToken ct = default)
    {
        try
        {
            await Task.Run(() => ExecuteWmiShutdown(ip, isReboot: false), ct);

            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"SHUTDOWN command sent to {serverName} ({ip})."), ct);
        }
        catch (Exception ex)
        {
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Shutdown of {serverName} ({ip}) FAILED: {ex.Message}"), ct);
        }
    }

    // ── WMI helper ────────────────────────────────────────────────────────────

    private static void ExecuteWmiShutdown(string ip, bool isReboot)
    {
        var scope = new ManagementScope(
            $@"\\{ip}\root\cimv2",
            new ConnectionOptions
            {
                Impersonation    = ImpersonationLevel.Impersonate,
                Authentication   = AuthenticationLevel.PacketPrivacy,
                EnablePrivileges = true,
                Timeout          = TimeSpan.FromSeconds(15)
            });

        scope.Connect();

        var query = new ObjectQuery("SELECT * FROM Win32_OperatingSystem WHERE Primary=true");
        using var searcher = new ManagementObjectSearcher(scope, query);
        using var results  = searcher.Get();

        foreach (ManagementObject os in results.Cast<ManagementObject>())
        {
            using (os)
            using (var inParams = os.GetMethodParameters("Win32Shutdown"))
            {
                // 6 = Reboot + Force, 5 = Shutdown + Force
                inParams["Flags"] = isReboot ? 6 : 5;
                inParams["Reserved"] = 0;

                using var outParams = os.InvokeMethod("Win32Shutdown", inParams, null);

                if (outParams != null)
                {
                    var returnValue = Convert.ToInt32(outParams["ReturnValue"]);
                    if (returnValue != 0)
                    {
                        throw new Exception($"WMI Error Code: {returnValue}. " +
                                            $"Check permissions, policies, or open applications.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Відкриває SSH сесію через PuTTY якщо встановлений,
    /// або через вбудований Windows SSH клієнт як fallback.
    /// </summary>
    public async Task OpenSshAsync(string ip, string name, CancellationToken ct = default)
    {
        if (!IsValidHostOrIp(ip))
        {
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Недійсний формат адреси для {name}: '{ip}'. SSH не запущено."), ct);
            return;
        }

        try
        {
            var puttyPaths = new[]
            {
                @"C:\Program Files\PuTTY\putty.exe",
                @"C:\Program Files (x86)\PuTTY\putty.exe",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Programs\PuTTY\putty.exe")
            };

            string? putty = puttyPaths.FirstOrDefault(File.Exists);

            if (putty is not null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = putty,
                    Arguments       = $"-ssh {ip}",
                    UseShellExecute = false
                });

                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    $"Launched PuTTY SSH to {name} ({ip})."), ct);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/k ssh {ip}",
                UseShellExecute = true,
                CreateNoWindow  = false
            });

            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                $"PuTTY not found. Launching Windows SSH to {name} ({ip})."), ct);
        }
        catch (Exception ex)
        {
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Failed to open SSH to {name} ({ip}): {ex.Message}"), ct);
        }
    }

    /// <summary>
    /// Валідує, що рядок є IP-адресою або доменним ім'ям без символів,
    /// здатних вплинути на розбір аргументів cmd.exe (наприклад &amp; | ^ " %).
    /// Захист на майбутнє: наразі ip завжди береться з довіреного
    /// appsettings.json, але цей метод унеможливлює command injection,
    /// якщо джерело колись стане динамічним (ручне додавання серверів тощо).
    /// </summary>
    private static bool IsValidHostOrIp(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        return System.Text.RegularExpressions.Regex.IsMatch(
            value, @"^[a-zA-Z0-9.\-]+$");
    }
}
