import clsx from 'clsx'
import styles from './ToggleSwitch.module.scss'

export interface ToggleSwitchProps {
  checked: boolean
  onChange: (checked: boolean) => void
  disabled?: boolean
  ariaLabel: string
}

/** Стилізований checkbox під вигляд on/off-перемикача (Settings → Monitoring Services, Крок 4 #7). */
export function ToggleSwitch({ checked, onChange, disabled, ariaLabel }: ToggleSwitchProps) {
  return (
    <label className={clsx(styles.switch, disabled && styles.disabled)}>
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
        aria-label={ariaLabel}
      />
      <span className={styles.track}>
        <span className={styles.thumb} />
      </span>
    </label>
  )
}
