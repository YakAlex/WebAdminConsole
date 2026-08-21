import {
  LayoutGrid,
  Activity,
  Clock,
  Cpu,
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

// Порядок і склад — буквально §4 брифу. Іконки — Lucide (§22: один стиль
// іконок на весь застосунок, line-стиль, stroke ~1.5-2px за замовчуванням
// у Lucide — нічого додатково налаштовувати не треба).
export const navItems: NavItem[] = [
  { path: '/', label: 'Overview', icon: LayoutGrid },
  { path: '/ping', label: 'Ping', icon: Activity },
  { path: '/uptime', label: 'Uptime', icon: Clock },
  { path: '/resources', label: 'Resources', icon: Cpu },
  { path: '/rdp-sessions', label: 'RDP Sessions', icon: Monitor },
  { path: '/zabbix-alerts', label: 'Zabbix Alerts', icon: Bell },
  { path: '/backups', label: 'Backups', icon: Database },
  { path: '/logs', label: 'Logs', icon: ScrollText },
]
