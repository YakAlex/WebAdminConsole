import styles from './Spinner.module.scss'

export interface SpinnerProps {
  /** Текст під спінером, напр. "Loading dashboard…". Необов'язковий. */
  label?: string
}

/** Крок 11.3 аудиту: єдиний спінер для loading-gate на сторінках замість "тихого" порожнього стану. */
export function Spinner({ label }: SpinnerProps) {
  return (
    <div className={styles.wrap} role="status" aria-live="polite">
      <span className={styles.ring} aria-hidden="true" />
      {label && <span className={styles.label}>{label}</span>}
    </div>
  )
}
