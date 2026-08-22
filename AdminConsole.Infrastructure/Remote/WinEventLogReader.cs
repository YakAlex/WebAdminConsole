using System.Diagnostics.Eventing.Reader;
using System.Net.NetworkInformation;
using AdminConsole.Domain.Models;
using EvtLogReader = System.Diagnostics.Eventing.Reader.EventLogReader;

namespace AdminConsole.Infrastructure.Remote;

/// <summary>
/// Shared logic for reading the Windows Event Log via EventLogQuery
/// (Eventing.Reader). Unlike System.Diagnostics.EventLog, Eventing.Reader
/// works through cursors and XPath queries — no indexed access, so it
/// stays stable under active log writes (no ArgumentException from Entries
/// changing mid-read).
///
/// Carried over unchanged (T4.6) — a static class, zero dependencies on WPF.
/// </summary>
public static class WinEventLogReader
{
    public const int FetchCount = 20;

    /// <summary>
    /// machineName: "." for localhost, or an IP/hostname for a remote machine.
    /// since: null = initial read (last FetchCount errors over 3 days),
    ///        not null = incremental mode (only newer than since).
    /// </summary>
    public static List<EventLogEntry> ReadErrors(string machineName, DateTimeOffset? since)
    {
        var results = new List<EventLogEntry>();

        foreach (var logName in new[] { "System", "Application" })
        {
            try
            {
                // XPath filter: only Error (Level=2) and FailureAudit (Keywords=0x10000000000000)
                // With a time bound so we don't scan the entire log.
                string timeFilter = since.HasValue
                    ? $"@SystemTime > '{since.Value.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffffZ}'"
                    : $"@SystemTime > '{DateTime.UtcNow.AddDays(-3):yyyy-MM-ddTHH:mm:ss.fffffffZ}'";

                // Level=2 → Error, Keywords includes FailureAudit (0x10000000000000)
                string xpath =
                    $"*[System[(Level=2 or Keywords='0x10000000000000') and TimeCreated[{timeFilter}]]]";

                // PathType.LogName — reads the live log (not a file).
                // Direction.Backward — newest to oldest, so we stop after FetchCount.
                var query = new EventLogQuery(logName, PathType.LogName, xpath)
                {
                    ReverseDirection = true,
                    Session          = machineName == "."
                        ? null
                        : new EventLogSession(machineName)
                };

                using var reader = new EvtLogReader(query);

                int read = 0;
                EventRecord? record;

                while (read < FetchCount * 2 && (record = reader.ReadEvent()) is not null)
                {
                    using (record)
                    {
                        results.Add(MapRecord(record));
                        read++;
                    }
                }
            }
            catch (EventLogNotFoundException)
            {
                // Log doesn't exist on this machine — skip silently
            }
            catch (UnauthorizedAccessException)
            {
                // No permission to read this log — skip silently
            }
            catch (Exception)
            {
                // RPC unavailable, network error, etc. — skip silently
            }
        }

        return results
            .OrderByDescending(e => e.TimeGenerated)
            .Take(FetchCount)
            .ToList();
    }

    private static EventLogEntry MapRecord(EventRecord r)
    {
        // FormatDescription() can return null if the provider isn't installed
        string rawMessage = string.Empty;
        try { rawMessage = r.FormatDescription() ?? string.Empty; }
        catch { rawMessage = r.Properties?.FirstOrDefault()?.Value?.ToString() ?? string.Empty; }

        // Level: 1=Critical, 2=Error, 3=Warning, 4=Information
        // Keywords: 0x10000000000000 = Audit Failure
        var severity = r.Level switch
        {
            1 => EventSeverity.Critical,
            2 => EventSeverity.Error,
            3 => EventSeverity.Warning,
            _ => EventSeverity.Information
        };

        // FailureAudit — if the Audit Failure keyword is present
        const long auditFailureKeyword = 0x10000000000000;
        if ((r.Keywords & auditFailureKeyword) != 0)
            severity = EventSeverity.Critical;

        return new EventLogEntry(
            Severity:      severity,
            Source:        r.ProviderName ?? "Unknown",
            Message:       TrimMessage(rawMessage),
            EventId:       r.Id.ToString(),
            TimeGenerated: r.TimeCreated.HasValue
                ? new DateTimeOffset(r.TimeCreated.Value)
                : DateTimeOffset.Now);
    }

    public static string TrimMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(no message)";
        var firstLine = raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;
        return firstLine.Length > 200 ? firstLine[..200] + "…" : firstLine;
    }

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
