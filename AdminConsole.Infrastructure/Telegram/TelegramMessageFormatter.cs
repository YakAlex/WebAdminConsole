using System.Text;
using AdminConsole.Domain.Models;

namespace AdminConsole.Infrastructure.Telegram;

/// <summary>
/// Pure HTML-formatted block builders for the Telegram bot's Backups and
/// Maintenance screens (redesign, 2026-08-23). Each returned entry is one
/// self-contained, fully-closed HTML fragment — TelegramTextChunker.BuildPages
/// treats every entry as an atomic unit, so a page break never lands inside
/// an open tag as long as each block closes every tag it opens.
/// </summary>
public static class TelegramMessageFormatter
{
    // ── Backups ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Groups checks by Name (the server's display identity, matching every
    /// other screen — Host is the technical hostname and isn't shown) so
    /// Full/Diff for the same server render as one block instead of two
    /// unrelated-looking lines. isHostUnderMaintenance is keyed by
    /// BackupCheckState.Host, matching the existing _serverLookup lookup.
    /// </summary>
    public static List<string> BuildBackupBlocks(
        IEnumerable<BackupCheckState> states,
        Func<string, bool> isHostUnderMaintenance)
    {
        var blocks = states
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => BuildBackupBlock(g.Key, g.OrderBy(s => s.Kind), isHostUnderMaintenance))
            .ToList();

        return blocks.Count == 0
            ? ["No backup checks configured (BackupChecks in appsettings.json)."]
            : blocks;
    }

    private static string BuildBackupBlock(
        string name, IEnumerable<BackupCheckState> checks, Func<string, bool> isHostUnderMaintenance)
    {
        var checksList = checks.ToList();
        bool underMaintenance = checksList.Count > 0 && isHostUnderMaintenance(checksList[0].Host);

        var sb = new StringBuilder();
        sb.Append($"<b>{TelegramHtml.Escape(name)}</b>");
        if (underMaintenance) sb.Append(" 🔧");

        foreach (var s in checksList)
        {
            string kindLabel = s.Kind == BackupKind.Diff ? "Diff" : "Full";
            string statusDisplay = s.Outcome == BackupOutcome.Ok
                ? (s.LastConfirmedAt is { } at ? $"<code>{at.ToLocalTime():dd.MM HH:mm}</code>" : "<i>unknown</i>")
                : $"<b>{TelegramHtml.Escape(s.Outcome.ToString().ToUpperInvariant())}</b>";

            sb.Append('\n').Append($"  {BackupIcon(s.Outcome)} {kindLabel} — {statusDisplay}");
        }

        if (underMaintenance)
            sb.Append('\n').Append("  <i>[Maintenance]</i>");

        return sb.ToString();
    }

    public static string BackupIcon(BackupOutcome outcome) => outcome switch
    {
        BackupOutcome.Ok          => "✅",
        BackupOutcome.SizeWarning => "⚠️",
        BackupOutcome.Stale       => "⏰",
        BackupOutcome.Missing     => "🚫",
        _                         => "❓"
    };

    // ── Maintenance ──────────────────────────────────────────────────────────

    public static List<string> BuildMaintenanceBlocks(IEnumerable<MaintenanceWindow> windows)
    {
        var blocks = windows
            .OrderBy(w => w.To ?? DateTimeOffset.MaxValue)
            .Select(BuildMaintenanceBlock)
            .ToList();

        return blocks.Count == 0 ? ["No active maintenance windows."] : blocks;
    }

    private static string BuildMaintenanceBlock(MaintenanceWindow w)
    {
        string reason = string.IsNullOrWhiteSpace(w.Reason) ? "No reason given" : w.Reason;
        string until = w.To is { } to
            ? $"until <code>{to.ToLocalTime():dd.MM HH:mm}</code>"
            : "<i>no time limit</i>";

        return $"<b>{TelegramHtml.Escape(w.DisplayName)}</b>\n" +
               $"  <i>{TelegramHtml.Escape(reason)}</i>\n" +
               $"  {until}";
    }
}
