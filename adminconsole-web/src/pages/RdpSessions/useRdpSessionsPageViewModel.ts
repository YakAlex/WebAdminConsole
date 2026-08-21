import { useRdpSessions } from '@/hooks/dashboard/useRdpSessions'
import { RdpSessionState } from '@/lib/api/types'

/**
 * Крок 3 (#4): data.sessions — плаский список Active + Disconnected (саме
 * так таблиця на сторінці може показати обидва стани разом). Але "Active
 * sessions"/"Unique users" у зведенні мають рахувати ЛИШЕ Active — раніше
 * рахували весь масив, тож відключена сесія помилково збільшувала лічильник.
 */
export function useRdpSessionsPageViewModel() {
  const data = useRdpSessions()
  const activeSessions = data.sessions.filter((s) => s.state === RdpSessionState.Active)
  const uniqueUsers = new Set(activeSessions.map((s) => s.username)).size

  return {
    sessions: data.sessions,
    activeCount: activeSessions.length,
    uniqueUsers,
    dailyPeak: data.dailyPeak,
    lastLogout: data.lastLogout,
  }
}
