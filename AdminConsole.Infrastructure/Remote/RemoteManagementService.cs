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
/// T4.9: an on-demand service, NOT a BackgroundService — actions are
/// triggered by a user click through ServersController (Priority 3, #3.1),
/// not by a polling loop.
/// </summary>
public sealed class RemoteManagementService(IMediator mediator)
{
    private const string LogSource = "RemoteMgmt";

    // Step 12 (Priority 3, #3.1): OpenContinuousPingAsync/OpenRdpAsync/
    // OpenSshAsync were removed — they called Process.Start(cmd.exe/mstsc.exe/
    // putty.exe) ON THE MACHINE running the service itself. In WPF that was
    // the admin's own computer (an interactive session); now it's a
    // headless Windows Service (Session 0 isolation, no desktop) — the
    // window simply wouldn't be shown to anyone, and even if it were, it
    // would appear on the server, not the admin's computer. The web-native
    // replacement: RDP → downloadable .rdp file (below), Continuous Ping →
    // the frontend itself polls the already-existing GET /api/ping while
    // the modal is open (no new backend call needed). SSH is out of scope.

    // ── Remote restart ────────────────────────────────────────────────────────

    /// <summary>
    /// Issues a WMI Win32_OperatingSystem.Reboot() call against the remote host.
    /// Requires the current user to have admin rights on the target machine.
    /// Returns (Success, Error) — exceptions used to just be logged and
    /// lost (fire-and-forget); now the REST controller can immediately tell
    /// the admin whether the command actually went through, rather than
    /// just "request sent".
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

    // Audit Zone 3, Finding #1 (2026-08-22): ConnectionOptions.Timeout only
    // bounds the scope.Connect() phase — not the query itself (searcher.Get())
    // or the method call (InvokeMethod). If the target server accepted the
    // connection and then "hung" (network drop, a stuck WMI service), both
    // could hang indefinitely — Task.Run(ct) wouldn't help (ct only means
    // "don't start if already canceled", it doesn't interrupt an
    // already-running synchronous call). The same timeout is now explicitly
    // applied to both the searcher's Options and InvokeMethodOptions — the
    // same pattern already correctly used in
    // RemoteEventLogService.QueryWmiEventLog.
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
