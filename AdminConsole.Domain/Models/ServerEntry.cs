namespace AdminConsole.Domain.Models;

public enum ServerType
{
    Windows,  // RDP + Restart + Shutdown
    Linux,    // SSH (PuTTY) — no RDP, no Restart/Shutdown
    Network   // switch/AP/NAS — ping only
}

/// <summary>A single entry from the "Servers" array in appsettings.json.</summary>
public sealed class ServerEntry
{
    public string     Name  { get; init; } = string.Empty;
    public string     IP    { get; init; } = string.Empty;
    public string     Group { get; init; } = string.Empty;

    /// <summary>
    /// Device type. Determines which buttons are shown in the Ping Dashboard.
    /// Defaults to Windows (backward compatibility with older
    /// appsettings.json files where the Type field is absent).
    /// </summary>
    public ServerType Type  { get; init; } = ServerType.Windows;
}
