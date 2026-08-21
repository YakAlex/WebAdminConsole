import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'

/**
 * T6.2: клієнт DashboardHub. withCredentials: true — обов'язково, інакше
 * Windows-автентифікація (Negotiate) не долетить до /hubs/dashboard навіть
 * через same-origin Vite-проксі.
 */
export function createDashboardConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl('/hubs/dashboard', { withCredentials: true })
    .withAutomaticReconnect([0, 2000, 5000, 10_000, 20_000, 30_000])
    .configureLogging(LogLevel.Warning)
    .build()
}
