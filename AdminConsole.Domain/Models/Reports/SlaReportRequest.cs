namespace AdminConsole.Domain.Models.Reports;

public sealed class SlaReportRequest
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To   { get; init; }
    public string? GroupFilter  { get; init; }   // null = all groups
    public string? ServerFilter { get; init; }   // null = all servers (substring of the name)
}
