import { ChevronDown } from 'lucide-react'
import styles from './FilterSelect.module.scss'

export interface FilterSelectOption {
  value: string
  label: string
}

export interface FilterSelectProps {
  value: string
  onChange: (value: string) => void
  options: FilterSelectOption[]
  ariaLabel: string
}

/** Стилізований нативний `<select>` під вигляд card-header dropdown-trigger (§ design system). */
export function FilterSelect({ value, onChange, options, ariaLabel }: FilterSelectProps) {
  return (
    <label className={styles.trigger}>
      <select
        className={styles.select}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        aria-label={ariaLabel}
      >
        {options.map((opt) => (
          <option key={opt.value} value={opt.value}>
            {opt.label}
          </option>
        ))}
      </select>
      <ChevronDown size={12} strokeWidth={2} className={styles.chevron} />
    </label>
  )
}
