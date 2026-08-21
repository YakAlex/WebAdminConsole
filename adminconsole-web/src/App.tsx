import type { ComponentType } from 'react'
import { BrowserRouter, Routes, Route } from 'react-router-dom'
import { AppLayout } from '@/components/layout/AppLayout/AppLayout'
import { Overview } from '@/pages/Overview/Overview'
import { Ping } from '@/pages/Ping/Ping'
import { Uptime } from '@/pages/Uptime/Uptime'
import { RdpSessions } from '@/pages/RdpSessions/RdpSessions'
import { ZabbixAlerts } from '@/pages/ZabbixAlerts/ZabbixAlerts'
import { Backups } from '@/pages/Backups/Backups'
import { Logs } from '@/pages/Logs/Logs'
import { Settings } from '@/pages/Settings/Settings'
import { AccessDenied } from '@/pages/AccessDenied/AccessDenied'
import { AuthChecking } from '@/pages/AuthChecking/AuthChecking'
import { useAuth } from '@/lib/auth/AuthContext'
import { navItems } from '@/components/layout/Sidebar/navItems'

const PAGES: Record<string, ComponentType> = {
  '/': Overview,
  '/ping': Ping,
  '/uptime': Uptime,
  '/rdp-sessions': RdpSessions,
  '/zabbix-alerts': ZabbixAlerts,
  '/backups': Backups,
  '/logs': Logs,
}

/**
 * Виправлення двох проблем з фідбеку користувача:
 *
 * 1. Flash of Unauthenticated Content — status === 'checking' рендерить
 *    ЛИШЕ AuthChecking, жодного AppLayout/маршруту ще не існує в дереві.
 * 2. AccessDenied всередині AppLayout — status === 'denied' рендерить
 *    AccessDenied ЗАМІСТЬ усього <BrowserRouter>, тож Sidebar/TopBar
 *    у принципі не монтуються для неавторизованого користувача.
 *
 * Лише status === 'authorized' монтує реальний застосунок.
 */
export function App() {
  const { status } = useAuth()

  if (status === 'checking') {
    return <AuthChecking />
  }

  if (status === 'denied') {
    return <AccessDenied />
  }

  return (
    <BrowserRouter>
      <Routes>
        <Route element={<AppLayout />}>
          {navItems.map(({ path }) => {
            const Page = PAGES[path]
            return <Route key={path} path={path} element={<Page />} />
          })}
          <Route path="/settings" element={<Settings />} />
        </Route>
      </Routes>
    </BrowserRouter>
  )
}
