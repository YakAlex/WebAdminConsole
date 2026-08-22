using System.Management;
using AdminConsole.Domain.Events;
using MediatR;

namespace AdminConsole.Infrastructure.Remote;

/// <summary>
/// Executes remote management actions against servers.
/// All methods are async and run WMI work on the thread pool.
/// Results (success or error) are published via AppLogEntryOccurred so
/// they appear in the Logs feed automatically.
///
/// WMI dependency: System.Management (NuGet package on net8.0-windows).
///
/// T4.9: on-demand сервіс, НЕ BackgroundService — дії ініціюються кліком
/// користувача через ServersController (Пріоритет 3, #3.1), не циклом опитування.
/// </summary>
public sealed class RemoteManagementService(IMediator mediator)
{
    private const string LogSource = "RemoteMgmt";

    // Крок 12 (Пріоритет 3, #3.1): OpenContinuousPingAsync/OpenRdpAsync/
    // OpenSshAsync видалені — вони викликали Process.Start(cmd.exe/mstsc.exe/
    // putty.exe) НА МАШИНІ, де крутиться сама служба. У WPF це був комп'ютер
    // адміна (інтерактивна сесія), тепер це headless Windows Service (Session
    // 0 isolation, без робочого стола) — вікно просто нікому не покажеться,
    // і навіть якби показалось, то не на комп'ютері адміна, а на сервері.
    // Веб-нативна заміна: RDP → .rdp-файл на скачування (нижче), Continuous
    // Ping → фронтенд сам опитує вже готовий GET /api/ping, поки відкрита
    // модалка (без нового бекенд-виклику). SSH — поза скоупом.

    // ── Remote restart ────────────────────────────────────────────────────────

    /// <summary>
    /// Issues a WMI Win32_OperatingSystem.Reboot() call against the remote host.
    /// Requires the current user to have admin rights on the target machine.
    /// Повертає (Success, Error) — раніше винятки лише логувались і губились
    /// (fire-and-forget); тепер REST-контролер може одразу повідомити адміна,
    /// чи команда реально прийнялась, а не лише "запит відправлено".
    /// </summary>
    public async Task<(bool Success, string? Error)> RemoteRestartAsync(string ip, string serverName, CancellationToken ct = default)
    {
        try
        {
            await Task.Run(() => ExecuteWmiShutdown(ip, isReboot: true), ct);

            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"RESTART command sent to {serverName} ({ip})."), ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Restart of {serverName} ({ip}) FAILED: {ex.Message}"), ct);
            return (false, ex.Message);
        }
    }

    // ── Remote shutdown ───────────────────────────────────────────────────────

    /// <summary>Issues a WMI Win32_OperatingSystem.Shutdown() call against the remote host.</summary>
    public async Task<(bool Success, string? Error)> RemoteShutdownAsync(string ip, string serverName, CancellationToken ct = default)
    {
        try
        {
            await Task.Run(() => ExecuteWmiShutdown(ip, isReboot: false), ct);

            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"SHUTDOWN command sent to {serverName} ({ip})."), ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Shutdown of {serverName} ({ip}) FAILED: {ex.Message}"), ct);
            return (false, ex.Message);
        }
    }

    // ── WMI helper ────────────────────────────────────────────────────────────

    // Аудит Зона 3, Знахідка №1 (2026-08-22): ConnectionOptions.Timeout
    // обмежує ЛИШЕ фазу scope.Connect() — не сам запит (searcher.Get()) чи
    // виклик методу (InvokeMethod). Якщо цільовий сервер прийняв з'єднання,
    // а потім "завис" (мережевий розрив, зависла WMI-служба), обидва могли
    // висіти необмежено довго — Task.Run(ct) цьому не завадив би (ct лише
    // "не запускай, якщо вже скасовано", не перериває вже запущений
    // синхронний виклик). Той самий таймаут тепер явно застосовано і на
    // Options пошуковика, і на InvokeMethodOptions — той самий патерн, що
    // вже коректно використаний у RemoteEventLogService.QueryWmiEventLog.
    private const int WmiTimeoutSeconds = 15;

    private static void ExecuteWmiShutdown(string ip, bool isReboot)
    {
        var scope = new ManagementScope(
            $@"\\{ip}\root\cimv2",
            new ConnectionOptions
            {
                Impersonation    = ImpersonationLevel.Impersonate,
                Authentication   = AuthenticationLevel.PacketPrivacy,
                EnablePrivileges = true,
                Timeout          = TimeSpan.FromSeconds(WmiTimeoutSeconds)
            });

        scope.Connect();

        var query = new ObjectQuery("SELECT * FROM Win32_OperatingSystem WHERE Primary=true");
        using var searcher = new ManagementObjectSearcher(scope, query)
        {
            Options = { Timeout = TimeSpan.FromSeconds(WmiTimeoutSeconds) }
        };
        using var results  = searcher.Get();

        var invokeOptions = new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(WmiTimeoutSeconds) };

        foreach (ManagementObject os in results.Cast<ManagementObject>())
        {
            using (os)
            using (var inParams = os.GetMethodParameters("Win32Shutdown"))
            {
                // 6 = Reboot + Force, 5 = Shutdown + Force
                inParams["Flags"] = isReboot ? 6 : 5;
                inParams["Reserved"] = 0;

                using var outParams = os.InvokeMethod("Win32Shutdown", inParams, invokeOptions);

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

}
