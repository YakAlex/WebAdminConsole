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
 * Fixes two issues from user feedback:
 *
 * 1. Flash of Unauthenticated Content — status === 'checking' renders
 *    ONLY AuthChecking; no AppLayout/route exists in the tree yet.
 * 2. AccessDenied inside AppLayout — status === 'denied' renders
 *    AccessDenied INSTEAD OF the entire <BrowserRouter>, so the
 *    Sidebar/TopBar never mount at all for an unauthorized user.
 *
 * Only status === 'authorized' mounts the real app.
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
