import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath, URL } from 'node:url'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    proxy: {
      // REST API — AdminConsole.Api (Kestrel, Phase 3).
      //
      // Target is the HTTPS endpoint (appsettings.json,
      // "Kestrel:Endpoints:Https", :5001) — NOT the old :5074
      // launchSettings.json port, and NOT the plain-HTTP :5000 endpoint.
      // appsettings.json now declares explicit Kestrel:Endpoints, which
      // Kestrel treats as taking precedence over ASPNETCORE_URLS/launch
      // profiles, so the backend actually listens on 5000/5001. And the
      // auth cookie is CookieSecurePolicy.Always (Program.cs) — the
      // browser silently drops it over plain HTTP, so the dev proxy has to
      // go through HTTPS too. `secure: false` tells the proxy to accept
      // the backend's self-signed dev certificate (README, "Local
      // Development — HTTPS certificate") instead of rejecting it as
      // untrusted.
      '/api': {
        target: 'https://localhost:5001',
        changeOrigin: true,
        secure: false,
      },
      // SignalR hub (DashboardHub, T3.7) — a separate ws: true flag,
      // otherwise Vite won't proxy the WebSocket upgrade for real-time events.
      '/hubs': {
        target: 'https://localhost:5001',
        changeOrigin: true,
        ws: true,
        secure: false,
      },
    },
  },
})
