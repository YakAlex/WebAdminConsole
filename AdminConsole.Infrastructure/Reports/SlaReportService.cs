using AdminConsole.Domain.Models;
using AdminConsole.Domain.Models.Reports;
using AdminConsole.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace AdminConsole.Infrastructure.Reports;

/// <summary>
/// Computes an SLA report over data already held in memory —
/// GetSnapshot() from UptimeTrackerService and the static server list
/// from appsettings.json (the same IOptions used by PingMonitorService,
/// with no dependency on the ping loop itself).
///
/// Generate() is a pure function of its input (Now is captured once at
/// the start) — there's no file I/O here at all, so the math can be
/// unit-tested independently of disk and UI.
///
/// T4.12: carried over unchanged (the service had no dependency on
/// IMessenger/file persistence anyway — Generate() was already a pure
/// function). Registered as an on-demand Singleton + called from
/// SlaReportJob (Hangfire, scheduled) and SlaController (REST, on-demand).
/// </summary>
public sealed class SlaReportService(
    IOptions<List<ServerEntry>> servers,
    UptimeTrackerService        uptime)
{
    private readonly IReadOnlyList<ServerEntry> _servers = servers.Value.AsReadOnly();

    /// <summary>
    /// "Fleet availability" for the period — the fraction of [from, to] during
    /// which at least ONE server was offline, via the union of downtime
    /// intervals.
    /// </summary>
    public double GetFleetAvailabilityPercent(DateTimeOffset from, DateTimeOffset to)
    {
        var now            = DateTimeOffset.Now;
        var effectiveTo    = Min(now, to);
        var periodDuration = effectiveTo - from;

        if (periodDuration <= TimeSpan.Zero) return 100.0;

        var intervals = uptime.GetSnapshot()
            .Select(r => (
                Start: Max(r.FellAt, from),
                End:   Min(r.RecoveredAt ?? now, effectiveTo)))
            .Where(iv => iv.End > iv.Start)
            .OrderBy(iv => iv.Start)
            .ToList();

        var totalDown = TimeSpan.Zero;
        DateTimeOffset? curStart = null;
        DateTimeOffset? curEnd   = null;

        foreach (var iv in intervals)
        {
            if (curEnd is null || iv.Start > curEnd.Value)
            {
                if (curStart is not null)
                    totalDown += curEnd!.Value - curStart.Value;

                curStart = iv.Start;
                curEnd   = iv.End;
            }
            else if (iv.End > curEnd.Value)
            {
                curEnd = iv.End;
            }
        }

        if (curStart is not null)
            totalDown += curEnd!.Value - curStart.Value;

        return Math.Clamp(
            100.0 - totalDown.TotalSeconds / periodDuration.TotalSeconds * 100.0,
            0.0, 100.0);
    }

    public SlaReport Generate(SlaReportRequest request)
    {
        var now  = DateTimeOffset.Now;
        var from = request.From;
        var to   = request.To;
        var effectiveTo    = Min(now, to);
        var periodDuration = effectiveTo - from;

        if (periodDuration <= TimeSpan.Zero)
        {
            return new SlaReport
            {
                GeneratedAt          = now,
                From                 = from,
                To                   = to,
                OverallUptimePercent = null,
                Servers              = Array.Empty<ServerSlaEntry>(),
                MaintenanceAppendix  = Array.Empty<IncidentDetail>()
            };
        }

        var liveServers = _servers
            .Where(s => MatchesFilters(s.Group, s.Name, request))
            .ToList();

        var records = uptime.GetSnapshot()
            .Where(r => MatchesFilters(r.ServerGroup, r.ServerName, request))
            .Where(r => ClippedDuration(r, from, effectiveTo, now) > TimeSpan.Zero)   // ← to → effectiveTo
            .ToList();

        var baseKeys = liveServers.Select(s => s.IP)
            .Union(records.Select(r => r.ServerIp))
            .Distinct()
            .ToList();

        var entries = new List<ServerSlaEntry>(baseKeys.Count);

        foreach (var ip in baseKeys)
        {
            var live          = liveServers.FirstOrDefault(s => s.IP == ip);
            var serverRecords = records.Where(r => r.ServerIp == ip).ToList();

            string name, group;
            if (live is not null)
            {
                name  = live.Name;
                group = live.Group;
            }
            else
            {
                // Removed from config — take the name/group from the last record.
                var last = serverRecords.OrderByDescending(r => r.FellAt).First();
                name  = last.ServerName;
                group = last.ServerGroup;
            }

            var downtime            = TimeSpan.Zero;
            var maintenanceDowntime = TimeSpan.Zero;
            var incidentCount       = 0;
            var closedDurations     = new List<TimeSpan>();

            foreach (var r in serverRecords)
            {
                var clipped = ClippedDuration(r, from, effectiveTo, now);

                if (r.ClosedByMaintenance)
                    maintenanceDowntime += clipped;
                else
                {
                    downtime += clipped;
                    incidentCount++;
                }
                if (r.RecoveredAt is not null && !r.ClosedByMaintenance)
                    closedDurations.Add(r.RecoveredAt.Value - r.FellAt);
            }

            var uptimePercent = Math.Clamp(
                (periodDuration - downtime) / periodDuration * 100.0, 0.0, 100.0);

            TimeSpan? mttr = closedDurations.Count > 0
                ? TimeSpan.FromTicks((long)closedDurations.Average(d => d.Ticks))
                : null;

            var incidentDetails = serverRecords
                .OrderByDescending(r => r.FellAt)
                .Select(r => ToIncidentDetail(r, from, effectiveTo, now))
                .ToList();

            entries.Add(new ServerSlaEntry
            {
                ServerName                  = name,
                ServerIp                    = ip,
                ServerGroup                 = group,
                IsRemovedFromMonitoring     = live is null,
                UptimePercent               = uptimePercent,
                DowntimeInPeriod            = downtime,
                MaintenanceDowntimeInPeriod = maintenanceDowntime,
                IncidentCount               = incidentCount,
                Mttr                        = mttr,
                Incidents                   = incidentDetails
            });
        }

        entries = entries.OrderBy(e => e.UptimePercent).ToList(); // worst first

        var maintenanceAppendix = records
            .Where(r => r.ClosedByMaintenance)
            .OrderBy(r => r.ServerName)
            .ThenByDescending(r => r.FellAt)
            .Select(r => ToIncidentDetail(r, from, effectiveTo, now))
            .ToList();

        double? overallUptimePercent;
        if (entries.Count == 0)
        {
            overallUptimePercent = null;
        }
        else
        {
            // double arithmetic is deliberate: periodDuration.Ticks * entries.Count
            // could overflow in a long on long periods × many servers.
            double totalDowntimeTicks = entries.Sum(e => (double)e.DowntimeInPeriod.Ticks);
            double totalPeriodTicks   = (double)periodDuration.Ticks * entries.Count;

            overallUptimePercent = Math.Clamp(
                100.0 - totalDowntimeTicks / totalPeriodTicks * 100.0, 0.0, 100.0);
        }

        return new SlaReport
        {
            GeneratedAt          = now,
            From                 = from,
            To                   = to,
            OverallUptimePercent = overallUptimePercent,
            Servers              = entries,
            MaintenanceAppendix  = maintenanceAppendix
        };
    }

    // ── Helper methods ─────────────────────────────────────────────────────

    /// <summary>
    /// A single formula for the whole service: EffectiveEnd = RecoveredAt ?? Min(Now, To),
    /// then intersect [FellAt, EffectiveEnd] with [From, To]. Equally correctly
    /// handles both an active incident on a live server and an "abandoned" open
    /// incident on a removed server — with no separate branches for either case.
    /// </summary>
    private static TimeSpan ClippedDuration(
        DowntimeRecord record, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        var effectiveEnd = record.RecoveredAt ?? Min(now, to);
        var clippedEnd   = Min(effectiveEnd, to);
        var clippedStart = Max(record.FellAt, from);

        var duration = clippedEnd - clippedStart;
        return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static IncidentDetail ToIncidentDetail(
        DowntimeRecord record, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
        => new()
        {
            ServerName          = record.ServerName,
            ServerGroup         = record.ServerGroup,
            FellAt              = record.FellAt,
            RecoveredAt         = record.RecoveredAt,
            EffectiveDuration   = ClippedDuration(record, from, to, now),
            IsOngoing           = record.RecoveredAt is null,
            ClosedByMaintenance = record.ClosedByMaintenance
        };

    private static bool MatchesFilters(string group, string name, SlaReportRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.GroupFilter) &&
            !group.Equals(request.GroupFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        // Substring match, not exact equality.
        if (!string.IsNullOrWhiteSpace(request.ServerFilter) &&
            !name.Contains(request.ServerFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
