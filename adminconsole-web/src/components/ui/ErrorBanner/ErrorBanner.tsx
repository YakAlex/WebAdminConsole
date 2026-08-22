import { TriangleAlert } from 'lucide-react'
import type { ApiError } from '@/lib/api/http'
import styles from './ErrorBanner.module.scss'

export interface ErrorBannerProps {
  /** What exactly failed to load, e.g. "servers", "backups". */
  context: string
  error: ApiError
}

/**
 * Audit step 11.3: a single error banner instead of a duplicated inline
 * div (previously the same shape of fragment lived separately in
 * Logs/Settings/Uptime). Doesn't hide the rest of the page — renders
 * ALONGSIDE data that's already loaded.
 */
export function ErrorBanner({ context, error }: ErrorBannerProps) {
  return (
    <div className={styles.banner}>
      <TriangleAlert size={14} strokeWidth={1.75} className={styles.icon} />
      <span>
        Failed to load {context}: {error.message} (HTTP {error.status})
      </span>
    </div>
  )
}
