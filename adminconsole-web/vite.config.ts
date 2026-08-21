import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath, URL } from 'node:url'
import { Agent } from 'node:http'

// Windows Negotiate (NTLM/Kerberos) — багатокроковий handshake, що
// прив'язується до ОДНОГО TCP-з'єднання між проксі й бекендом. Дефолтний
// http-proxy-агент Node НЕ тримає keep-alive і відкриває нове з'єднання на
// кожен запит — бекенд щоразу бачить "нового" анонімного клієнта і
// нескінченно повторює 401-виклик (перевірено вручну: curl --negotiate
// через проксі отримував Connection: close і зависав у 401-циклі, тоді як
// прямий запит на :5074 завершувався за 2 кроки). Один спільний keep-alive
// Agent на /api і /hubs — обов'язковий, інакше жоден захищений запит
// ніколи не долетить успішно через дев-проксі.
const keepAliveAgent = new Agent({ keepAlive: true })

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
      // REST API — AdminConsole.Api (Kestrel, Фаза 3). changeOrigin потрібен,
      // бо бекенд перевіряє Host-заголовок для Negotiate/Windows-автентифікації.
      '/api': {
        target: 'http://localhost:5074',
        changeOrigin: true,
        agent: keepAliveAgent,
      },
      // SignalR hub (DashboardHub, T3.7) — окремий прапорець ws: true,
      // інакше Vite не проксіює WebSocket upgrade для реал-тайм подій.
      '/hubs': {
        target: 'http://localhost:5074',
        changeOrigin: true,
        ws: true,
        agent: keepAliveAgent,
      },
    },
  },
})
