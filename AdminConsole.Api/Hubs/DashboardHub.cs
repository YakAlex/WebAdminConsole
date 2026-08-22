using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AdminConsole.Api.Hubs;

/// <summary>
/// Real-time channel for the React dashboard. Groups by data type — "ping",
/// "uptime", "backups", "logs" — the client subscribes only to the group(s)
/// shown by the current page, rather than receiving the entire event stream
/// at once.
/// </summary>
[Authorize(Policy = "Viewer")]
public sealed class DashboardHub : Hub
{
    public Task JoinGroup(string groupName) =>
        Groups.AddToGroupAsync(Context.ConnectionId, groupName);

    public Task LeaveGroup(string groupName) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
}
