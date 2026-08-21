using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AdminConsole.Api.Hubs;

/// <summary>
/// Реал-тайм канал для React-дашборду. Групи по типу даних — "ping",
/// "uptime", "backups", "logs" — клієнт підписується лише на групу(и), що
/// показує поточна сторінка, а не отримує весь потік подій одразу.
/// </summary>
[Authorize(Policy = "Viewer")]
public sealed class DashboardHub : Hub
{
    public Task JoinGroup(string groupName) =>
        Groups.AddToGroupAsync(Context.ConnectionId, groupName);

    public Task LeaveGroup(string groupName) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
}
