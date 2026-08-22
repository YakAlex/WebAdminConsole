import { useRdpSessions } from '@/hooks/dashboard/useRdpSessions'
import { RdpSessionState } from '@/lib/api/types'

/**
 * Step 3 (#4): data.sessions is a flat list of Active + Disconnected (this
 * is how the table on the page can show both states together). But
 * "Active sessions"/"Unique users" in the summary must count ONLY Active —
 * previously they counted the whole array, so a disconnected session would
 * incorrectly inflate the counter.
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
    loading: data.loading,
    fetchError: data.error,
  }
}
