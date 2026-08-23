using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Telegram;

namespace AdminConsole.Tests.Telegram;

public sealed class TelegramMessageFormatterTests
{
    private static BackupCheckState MakeState(
        string name, string host, BackupKind kind, BackupOutcome outcome, DateTimeOffset? lastConfirmedAt = null) => new()
    {
        Name = name,
        Host = host,
        Kind = kind,
        Outcome = outcome,
        LastConfirmedAt = lastConfirmedAt
    };

    [Fact]
    public void BuildBackupBlocks_NoStates_ReturnsPlaceholderMessage()
    {
        var blocks = TelegramMessageFormatter.BuildBackupBlocks([], _ => false);

        Assert.Single(blocks);
        Assert.Contains("No backup checks configured", blocks[0]);
    }

    [Fact]
    public void BuildBackupBlocks_GroupsFullAndDiffOfTheSameServer_IntoOneBlock()
    {
        var states = new[]
        {
            MakeState("Server1", "server1.local", BackupKind.Full, BackupOutcome.Ok, new DateTimeOffset(2026, 8, 23, 3, 0, 0, TimeSpan.Zero)),
            MakeState("Server1", "server1.local", BackupKind.Diff, BackupOutcome.Ok, new DateTimeOffset(2026, 8, 23, 15, 0, 0, TimeSpan.Zero)),
        };

        var blocks = TelegramMessageFormatter.BuildBackupBlocks(states, _ => false);

        var block = Assert.Single(blocks);
        Assert.Contains("<b>Server1</b>", block);
        Assert.Contains("Full", block);
        Assert.Contains("Diff", block);
    }

    [Fact]
    public void BuildBackupBlocks_DifferentServers_ProduceSeparateBlocks()
    {
        var states = new[]
        {
            MakeState("Server1", "server1.local", BackupKind.Full, BackupOutcome.Ok),
            MakeState("Server2", "server2.local", BackupKind.Full, BackupOutcome.Ok),
        };

        var blocks = TelegramMessageFormatter.BuildBackupBlocks(states, _ => false);

        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void BuildBackupBlocks_OkOutcome_ShowsLastConfirmedTimeAsCode()
    {
        // Expressed directly in the local offset (not converted from UTC) so
        // the formatter's internal .ToLocalTime() is a guaranteed no-op —
        // the assertion doesn't depend on the test machine's timezone.
        var localTime = new DateTimeOffset(2026, 8, 23, 3, 0, 0, DateTimeOffset.Now.Offset);
        var states = new[]
        {
            MakeState("Server1", "server1.local", BackupKind.Full, BackupOutcome.Ok, localTime)
        };

        var blocks = TelegramMessageFormatter.BuildBackupBlocks(states, _ => false);

        Assert.Contains("<code>23.08 03:00</code>", blocks[0]);
    }

    [Fact]
    public void BuildBackupBlocks_NonOkOutcome_ShowsOutcomeInBold_NotTheStaleTimestamp()
    {
        var states = new[]
        {
            MakeState("Server1", "server1.local", BackupKind.Full, BackupOutcome.Missing)
        };

        var blocks = TelegramMessageFormatter.BuildBackupBlocks(states, _ => false);

        Assert.Contains("<b>MISSING</b>", blocks[0]);
    }

    [Fact]
    public void BuildBackupBlocks_HostUnderMaintenance_AddsBadgeAndFooterNote()
    {
        var states = new[] { MakeState("Server1", "server1.local", BackupKind.Full, BackupOutcome.Ok) };

        var blocks = TelegramMessageFormatter.BuildBackupBlocks(states, host => host == "server1.local");

        Assert.Contains("🔧", blocks[0]);
        Assert.Contains("[Maintenance]", blocks[0]);
    }

    [Fact]
    public void BuildBackupBlocks_ServerNameWithHtmlSpecialChars_IsEscaped()
    {
        var states = new[] { MakeState("<script>", "host", BackupKind.Full, BackupOutcome.Ok) };

        var blocks = TelegramMessageFormatter.BuildBackupBlocks(states, _ => false);

        Assert.DoesNotContain("<script>", blocks[0]);
        Assert.Contains("&lt;script&gt;", blocks[0]);
    }

    // ── Maintenance ──────────────────────────────────────────────────────────

    private static MaintenanceWindow MakeWindow(
        string displayName, string reason = "", DateTimeOffset? to = null) => new()
    {
        ServerIp = "10.0.0.1",
        DisplayName = displayName,
        From = DateTimeOffset.Now,
        To = to,
        Reason = reason
    };

    [Fact]
    public void BuildMaintenanceBlocks_NoWindows_ReturnsPlaceholderMessage()
    {
        var blocks = TelegramMessageFormatter.BuildMaintenanceBlocks([]);

        Assert.Single(blocks);
        Assert.Contains("No active maintenance windows", blocks[0]);
    }

    [Fact]
    public void BuildMaintenanceBlocks_WithEndTime_ShowsUntilTimeAsCode()
    {
        // Local offset by construction — see the note in
        // BuildBackupBlocks_OkOutcome_ShowsLastConfirmedTimeAsCode above.
        var to = new DateTimeOffset(2026, 8, 23, 18, 0, 0, DateTimeOffset.Now.Offset);
        var blocks = TelegramMessageFormatter.BuildMaintenanceBlocks([MakeWindow("Server1", "Planned update", to)]);

        Assert.Contains("<b>Server1</b>", blocks[0]);
        Assert.Contains("<i>Planned update</i>", blocks[0]);
        Assert.Contains("<code>23.08 18:00</code>", blocks[0]);
    }

    [Fact]
    public void BuildMaintenanceBlocks_NoEndTime_ShowsNoTimeLimit()
    {
        var blocks = TelegramMessageFormatter.BuildMaintenanceBlocks([MakeWindow("Server1", "Ongoing work", to: null)]);

        Assert.Contains("no time limit", blocks[0]);
    }

    [Fact]
    public void BuildMaintenanceBlocks_EmptyReason_ShowsPlaceholderReason()
    {
        var blocks = TelegramMessageFormatter.BuildMaintenanceBlocks([MakeWindow("Server1", reason: "")]);

        Assert.Contains("No reason given", blocks[0]);
    }

    [Fact]
    public void BuildMaintenanceBlocks_ReasonWithHtmlSpecialChars_IsEscaped()
    {
        var blocks = TelegramMessageFormatter.BuildMaintenanceBlocks([MakeWindow("Server1", reason: "<b>hack</b>")]);

        Assert.DoesNotContain("<b>hack</b>", blocks[0]);
        Assert.Contains("&lt;b&gt;hack&lt;/b&gt;", blocks[0]);
    }
}
