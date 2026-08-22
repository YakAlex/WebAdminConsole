using System.Management;
using AdminConsole.Domain.Models;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Infrastructure.Remote;

/// <summary>
/// On-demand reading of the Event Log from a remote Windows machine.
///
/// IMPORTANT: unlike the local EventLogService, which uses
/// System.Diagnostics.EventLog(name, ".") — for remote machines this API
/// is unreliable with bare IP addresses (requires NetBIOS resolution,
/// easily hangs or silently returns 0 records). So here reads go through
/// WMI (Win32_NTLogEvent) — the same ManagementScope/DCOM path.
///
/// Protection against RPC timeouts: before attempting a WMI read — a quick
/// ping check (max 1.5s). If the server doesn't respond, we immediately
/// return an empty result with a reason, instead of waiting 30-60 seconds
/// for a DCOM timeout.
///
/// T4.7: carried over unchanged — no dependency on the messenger/mediator,
/// the on-demand result is returned directly to the caller (a future API
/// controller, Phase 6).
/// </summary>
public sealed class RemoteEventLogService(ILogger<RemoteEventLogService> logger)
{
    private const int PingTimeoutMs     = 1500;
    private const int WmiTimeoutSeconds = 8;
    private const int FetchCount        = 20;

    // Win32_NTLogEvent.EventType: 1=Error, 2=Warning, 3=Information,
    // 4=Security Audit Success, 5=Security Audit Failure
    private const int EventTypeError = 1;

    public async Task<RemoteEventLogResult> FetchAsync(
        string machineNameOrIp,
        CancellationToken ct = default)
    {
        bool reachable = await WinEventLogReader
            .IsReachableAsync(machineNameOrIp, PingTimeoutMs, ct)
            .ConfigureAwait(false);

        if (!reachable)
        {
            logger.LogDebug(
                "RemoteEventLogService: {Machine} unreachable — skipping Event Log fetch.",
                machineNameOrIp);

            return RemoteEventLogResult.Unreachable();
        }

        try
        {
            var entries = await Task
                .Run(() => QueryWmiEventLog(machineNameOrIp, ct), ct)
                .ConfigureAwait(false);

            return RemoteEventLogResult.Success(entries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex,
                "RemoteEventLogService: access denied querying Event Log on {Machine}.",
                machineNameOrIp);

            return RemoteEventLogResult.Failed(
                "Access denied — current user lacks permissions on this machine.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "RemoteEventLogService: failed to read Event Log from {Machine}.",
                machineNameOrIp);

            return RemoteEventLogResult.Failed(ex.Message);
        }
    }

    // ── WMI ───────────────────────────────────────────────────────────────────

    private static List<EventLogEntry> QueryWmiEventLog(string machineName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var scope = new ManagementScope(
            $@"\\{machineName}\root\cimv2",
            new ConnectionOptions
            {
                Timeout          = TimeSpan.FromSeconds(WmiTimeoutSeconds),
                Impersonation    = ImpersonationLevel.Impersonate,
                EnablePrivileges = true
            });

        scope.Connect();

        ct.ThrowIfCancellationRequested();

        var results = new List<EventLogEntry>();
        DateTime cutoff = DateTime.Now.AddDays(-3);
        string dmtfCutoff = ManagementDateTimeConverter.ToDmtfDateTime(cutoff);

        foreach (var logName in new[] { "System", "Application" })
        {
            ct.ThrowIfCancellationRequested();

            var query = new ObjectQuery(
                $"SELECT TimeGenerated, SourceName, Message, EventCode, EventType " +
                $"FROM Win32_NTLogEvent " +
                $"WHERE LogFile='{logName}' " +
                $"AND EventType={EventTypeError} " +
                $"AND TimeGenerated >= '{dmtfCutoff}'");

            using var searcher = new ManagementObjectSearcher(scope, query)
            {
                Options = { Timeout = TimeSpan.FromSeconds(WmiTimeoutSeconds) }
            };

            using var collection = searcher.Get();

            int scanned = 0;
            const int maxScanPerLog = 500;

            foreach (ManagementObject mo in collection)
            {
                if (ct.IsCancellationRequested) break;

                using (mo)
                {
                    if (++scanned > maxScanPerLog) break;
                    results.Add(MapWmiEvent(mo));
                }
            }
        }

        return results
            .OrderByDescending(e => e.TimeGenerated)
            .Take(FetchCount)
            .ToList();
    }

    private static EventLogEntry MapWmiEvent(ManagementObject mo)
    {
        string timeGeneratedRaw = mo["TimeGenerated"]?.ToString() ?? string.Empty;
        DateTimeOffset timestamp = ParseWmiDateTime(timeGeneratedRaw);

        string source  = mo["SourceName"]?.ToString() ?? "Unknown";
        string message = WinEventLogReader.TrimMessage(mo["Message"]?.ToString() ?? string.Empty);
        string eventId = mo["EventCode"]?.ToString() ?? "0";

        return new EventLogEntry(
            Severity:      EventSeverity.Error, // the query is already filtered to EventType=1
            Source:        source,
            Message:       message,
            EventId:       eventId,
            TimeGenerated: timestamp);
    }

    /// <summary>
    /// WMI returns time in DMTF format: "yyyyMMddHHmmss.ffffff+UUU"
    /// (UUU — offset in minutes from UTC). ManagementDateTimeConverter
    /// converts it the official way instead of manual parsing.
    /// </summary>
    private static DateTimeOffset ParseWmiDateTime(string dmtf)
    {
        if (string.IsNullOrWhiteSpace(dmtf)) return DateTimeOffset.MinValue;

        try
        {
            var dt = ManagementDateTimeConverter.ToDateTime(dmtf);
            return new DateTimeOffset(dt);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }
}

/// <summary>Result of an attempt to read a remote Event Log.</summary>
public sealed class RemoteEventLogResult
{
    public bool                          IsReachable  { get; private init; }
    public bool                          IsSuccess    { get; private init; }
    public string?                       ErrorMessage { get; private init; }
    public IReadOnlyList<EventLogEntry>  Entries      { get; private init; } = [];

    public static RemoteEventLogResult Unreachable() => new()
    {
        IsReachable  = false,
        IsSuccess    = false,
        ErrorMessage = "Server is offline or unreachable."
    };

    public static RemoteEventLogResult Success(IReadOnlyList<EventLogEntry> entries) => new()
    {
        IsReachable = true,
        IsSuccess   = true,
        Entries     = entries
    };

    public static RemoteEventLogResult Failed(string error) => new()
    {
        IsReachable  = true,
        IsSuccess    = false,
        ErrorMessage = error
    };
}
