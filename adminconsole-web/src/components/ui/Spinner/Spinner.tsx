import styles from './Spinner.module.scss'

export interface SpinnerProps {
  /** Text under the spinner, e.g. "Loading dashboard…". Optional. */
  label?: string
}

/** Audit step 11.3: a single spinner for page loading gates instead of a "silent" empty state. */
export function Spinner({ label }: SpinnerProps) {
  return (
    <div className={styles.wrap} role="status" aria-live="polite">
      <span className={styles.ring} aria-hidden="true" />
      {label && <span className={styles.label}>{label}</span>}
    </div>
  )
}
