import {
  LayoutGrid,
  Activity,
  Clock,
  Monitor,
  Bell,
  Database,
  ScrollText,
  type LucideIcon,
} from 'lucide-react'

export interface NavItem {
  path: string
  label: string
  icon: LucideIcon
}

// Order and composition — literally brief §4. Icons — Lucide (§22: a
// single icon style across the whole app, line style, stroke ~1.5-2px by
// default in Lucide — nothing extra to configure).
export const navItems: NavItem[] = [
  { path: '/', label: 'Overview', icon: LayoutGrid },
  { path: '/ping', label: 'Ping', icon: Activity },
  { path: '/uptime', label: 'Uptime', icon: Clock },
  { path: '/rdp-sessions', label: 'RDP Sessions', icon: Monitor },
  { path: '/zabbix-alerts', label: 'Zabbix Alerts', icon: Bell },
  { path: '/backups', label: 'Backups', icon: Database },
  { path: '/logs', label: 'Logs', icon: ScrollText },
]
