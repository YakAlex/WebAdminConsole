import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'

/**
 * T6.2: DashboardHub client. withCredentials: true is mandatory,
 * otherwise Windows authentication (Negotiate) won't reach
 * /hubs/dashboard even through the same-origin Vite proxy.
 */
export function createDashboardConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl('/hubs/dashboard', { withCredentials: true })
    .withAutomaticReconnect([0, 2000, 5000, 10_000, 20_000, 30_000])
    .configureLogging(LogLevel.Warning)
    .build()
}
