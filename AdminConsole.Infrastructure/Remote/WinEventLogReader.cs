using System.Net.NetworkInformation;

namespace AdminConsole.Infrastructure.Remote;

/// <summary>
/// Bug fix (2026-08-23, audit Finding 3.1): this class used to also contain
/// ReadErrors/MapRecord/TrimMessage for reading the Windows Event Log — that
/// whole feature (this class's Event-Log-specific half, plus EventLogService
/// and RemoteEventLogService) was fully built, registered, and running in
/// the background, but had zero consumers anywhere in the product (no
/// controller ever called RemoteEventLogService.FetchAsync, and no frontend
/// code ever subscribed to EventLogUpdatedOccurred). Removed per YAGNI —
/// recoverable from git history if this is needed later. IsReachableAsync
/// survives because BackupCheckEvaluator uses it as a generic pre-check
/// before scanning a UNC path.
/// </summary>
public static class WinEventLogReader
{
    public static async Task<bool> IsReachableAsync(
        string host, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping
                .SendPingAsync(host, timeoutMs)
                .WaitAsync(ct)
                .ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }
}
