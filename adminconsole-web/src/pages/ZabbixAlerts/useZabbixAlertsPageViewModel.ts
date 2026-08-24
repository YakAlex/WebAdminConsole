import { useZabbixProblems } from '@/hooks/dashboard/useZabbixProblems'
import { ZabbixSeverity } from '@/lib/api/types'

export function useZabbixAlertsPageViewModel() {
  const zabbixQuery = useZabbixProblems()
  const problems = zabbixQuery.payload?.problems ?? []

  const critical = problems.filter((p) => p.severity === ZabbixSeverity.High || p.severity === ZabbixSeverity.Disaster).length
  const warning = problems.filter((p) => p.severity === ZabbixSeverity.Average || p.severity === ZabbixSeverity.Warning).length
  const info = problems.filter(
    (p) => p.severity === ZabbixSeverity.Information || p.severity === ZabbixSeverity.NotClassified,
  ).length

  return {
    problems,
    critical,
    warning,
    info,
    hiddenCount: zabbixQuery.payload?.hiddenCount ?? 0,
    errorMessage: zabbixQuery.payload?.errorMessage ?? null,
    loading: zabbixQuery.loading,
    fetchError: zabbixQuery.error,
  }
}
