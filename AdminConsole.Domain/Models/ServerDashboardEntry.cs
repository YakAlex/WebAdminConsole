namespace AdminConsole.Domain.Models;

/// <summary>
/// An entry in the Server Dashboard ComboBox.
/// Can be "localhost" (the local machine) or any ServerEntry from appsettings.
/// Only Windows servers are shown — Linux and Network devices don't support WMI/EventLog.
/// </summary>
public sealed class ServerDashboardEntry
{
    public string Name       { get; }
    public string IP         { get; }
    public bool   IsLocal    { get; }

    /// <summary>Current ping status — updated from PingBatchResultMessage.</summary>
    public PingStatus PingStatus { get; set; } = PingStatus.Unknown;

    private ServerDashboardEntry(string name, string ip, bool isLocal)
    {
        Name    = name;
        IP      = ip;
        IsLocal = isLocal;
    }

    public static ServerDashboardEntry Localhost() =>
        new("localhost (this machine)", "127.0.0.1", isLocal: true);

    public static ServerDashboardEntry FromServerEntry(ServerEntry entry) =>
        new(entry.Name, entry.IP, isLocal: false);

    // Displayed by the ComboBox
    public override string ToString() => Name;
}
