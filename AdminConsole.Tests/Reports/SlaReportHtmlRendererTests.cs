using AdminConsole.Domain.Models.Reports;
using AdminConsole.Infrastructure.Reports;

namespace AdminConsole.Tests.Reports;

/// <summary>
/// SlaReportHtmlRenderer is a pure string -> string function (per its own
/// doc comment) — tested directly against hand-built SlaReport instances,
/// no DB/DI needed.
/// </summary>
public sealed class SlaReportHtmlRendererTests
{
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To   = new(2026, 1, 8, 0, 0, 0, TimeSpan.Zero);

    private static ServerSlaEntry MakeServer(
        string name, string group, double uptimePercent, TimeSpan downtime,
        IReadOnlyList<IncidentDetail>? incidents = null) => new()
    {
        ServerName = name,
        ServerIp = "10.0.0.1",
        ServerGroup = group,
        IsRemovedFromMonitoring = false,
        UptimePercent = uptimePercent,
        DowntimeInPeriod = downtime,
        MaintenanceDowntimeInPeriod = TimeSpan.Zero,
        IncidentCount = incidents?.Count ?? 0,
        Mttr = null,
        Incidents = incidents ?? []
    };

    private static SlaReport MakeReport(
        IReadOnlyList<ServerSlaEntry> servers, double? overallUptimePercent) => new()
    {
        GeneratedAt = DateTimeOffset.Now,
        From = From,
        To = To,
        OverallUptimePercent = overallUptimePercent,
        Servers = servers,
        MaintenanceAppendix = []
    };

    [Fact]
    public void Render_EscapesHtmlInServerAndGroupNames()
    {
        var malicious = "<script>alert(1)</script>";
        var report = MakeReport(
            [MakeServer(malicious, malicious, 100.0, TimeSpan.Zero)],
            overallUptimePercent: 100.0);

        var html = SlaReportHtmlRenderer.Render(report);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Render_NeverShowsExactly100Percent_WhenDowntimeIsNonZero()
    {
        // 99.9995% rounds to 100.00 at 2 decimals, but real downtime exists —
        // the renderer must clamp this down to 99.99%, never show a
        // misleading "100.00%".
        var report = MakeReport(
            [MakeServer("Server1", "Core", 99.9995, TimeSpan.FromSeconds(1))],
            overallUptimePercent: 99.9995);

        var html = SlaReportHtmlRenderer.Render(report);

        Assert.DoesNotContain("100.00%", html);
        Assert.Contains("99.99%", html);
    }

    [Fact]
    public void Render_ShowsExactly100Percent_WhenDowntimeIsZero()
    {
        var report = MakeReport(
            [MakeServer("Server1", "Core", 100.0, TimeSpan.Zero)],
            overallUptimePercent: 100.0);

        var html = SlaReportHtmlRenderer.Render(report);

        Assert.Contains("100.00%", html);
    }

    [Fact]
    public void Render_OmitsIncidentDetailsSection_WhenNoServerHasIncidents()
    {
        var report = MakeReport(
            [MakeServer("Server1", "Core", 100.0, TimeSpan.Zero, incidents: [])],
            overallUptimePercent: 100.0);

        var html = SlaReportHtmlRenderer.Render(report);

        Assert.DoesNotContain("Incident details", html);
    }

    [Fact]
    public void Render_IncludesIncidentDetailsSection_WhenAnIncidentExists()
    {
        var incident = new IncidentDetail
        {
            ServerName = "Server1",
            ServerGroup = "Core",
            FellAt = From.AddHours(1),
            RecoveredAt = From.AddHours(2),
            EffectiveDuration = TimeSpan.FromHours(1),
            IsOngoing = false,
            ClosedByMaintenance = false
        };

        var report = MakeReport(
            [MakeServer("Server1", "Core", 99.0, TimeSpan.FromHours(1), incidents: [incident])],
            overallUptimePercent: 99.0);

        var html = SlaReportHtmlRenderer.Render(report);

        Assert.Contains("Incident details", html);
    }

    [Fact]
    public void Render_ShowsOngoingBadge_ForIncidentWithNoRecoveredAt()
    {
        var incident = new IncidentDetail
        {
            ServerName = "Server1",
            ServerGroup = "Core",
            FellAt = From.AddHours(1),
            RecoveredAt = null,
            EffectiveDuration = TimeSpan.FromHours(1),
            IsOngoing = true,
            ClosedByMaintenance = false
        };

        var report = MakeReport(
            [MakeServer("Server1", "Core", 99.0, TimeSpan.FromHours(1), incidents: [incident])],
            overallUptimePercent: 99.0);

        var html = SlaReportHtmlRenderer.Render(report);

        Assert.Contains("badge-ongoing", html);
    }

    [Fact]
    public void Render_NoServers_ShowsDashForOverallUptime()
    {
        var report = MakeReport([], overallUptimePercent: null);

        var html = SlaReportHtmlRenderer.Render(report);

        Assert.Contains("<div class=\"value\">—</div>", html);
    }
}
