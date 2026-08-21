import { TriangleAlert } from 'lucide-react'
import type { ApiError } from '@/lib/api/http'
import styles from './ErrorBanner.module.scss'

export interface ErrorBannerProps {
  /** Що саме не завантажилось, напр. "servers", "backups". */
  context: string
  error: ApiError
}

/**
 * Крок 11.3 аудиту: єдиний банер помилки замість дубльованого інлайн-div'а
 * (раніше однаковий за формою фрагмент жив окремо в Logs/Settings/Uptime).
 * Не ховає решту сторінки — рендериться ПОРУЧ із уже завантаженими даними.
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
