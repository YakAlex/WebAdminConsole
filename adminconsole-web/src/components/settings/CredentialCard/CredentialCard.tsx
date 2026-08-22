import { useState, type ReactNode } from 'react'
import { KeyRound } from 'lucide-react'
import { Card, CardHeader } from '@/components/ui/Card'
import { StatusDot } from '@/components/ui/StatusDot'
import { ApiError } from '@/lib/api/http'
import styles from './CredentialCard.module.scss'

export interface CredentialSaveResult {
  ok: boolean
  message: string
}

export interface CredentialCardProps {
  title: string
  icon: ReactNode
  description: string
  hasCredentials: boolean
  maskedValue: string
  inputLabel: string
  onSave: (value: string) => Promise<CredentialSaveResult | void>
  onClear: () => Promise<void>
}

/**
 * §T6.2 item 2 (Settings → Credentials): one card per secret (Zabbix
 * Token, Telegram Bot Token) — status + password field + Save/Clear. One
 * component, two usage sites (Settings.tsx) instead of duplicating markup.
 */
export function CredentialCard({
  title,
  icon,
  description,
  hasCredentials,
  maskedValue,
  inputLabel,
  onSave,
  onClear,
}: CredentialCardProps) {
  const [value, setValue] = useState('')
  const [saving, setSaving] = useState(false)
  const [clearing, setClearing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saveResult, setSaveResult] = useState<CredentialSaveResult | null>(null)

  const busy = saving || clearing

  const handleSave = async () => {
    if (!value.trim()) return
    setSaving(true)
    setError(null)
    setSaveResult(null)
    try {
      const result = await onSave(value.trim())
      setValue('')
      if (result) setSaveResult(result)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to save.')
    } finally {
      setSaving(false)
    }
  }

  const handleClear = async () => {
    setClearing(true)
    setError(null)
    setSaveResult(null)
    try {
      await onClear()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to clear.')
    } finally {
      setClearing(false)
    }
  }

  return (
    <Card>
      <CardHeader eyebrow={title} icon={icon} />
      <span className={styles.description}>{description}</span>

      <div className={styles.status}>
        <StatusDot tone={hasCredentials ? 'success' : 'inactive'} />
        <span className={styles.statusLabel}>{hasCredentials ? 'Configured' : 'Not configured'}</span>
        {hasCredentials && <span className={styles.statusValue}>{maskedValue}</span>}
      </div>

      <div className={styles.form}>
        <KeyRound size={16} strokeWidth={1.75} aria-hidden="true" />
        <input
          type="password"
          className={styles.input}
          placeholder={inputLabel}
          value={value}
          onChange={(e) => setValue(e.target.value)}
          disabled={busy}
          autoComplete="off"
        />
      </div>

      <div className={styles.actions}>
        <button
          type="button"
          className={`${styles.button} ${styles.buttonPrimary}`}
          onClick={handleSave}
          disabled={busy || !value.trim()}
        >
          {saving ? 'Saving…' : 'Save'}
        </button>
        <button
          type="button"
          className={`${styles.button} ${styles.buttonDanger}`}
          onClick={handleClear}
          disabled={busy || !hasCredentials}
        >
          {clearing ? 'Clearing…' : 'Clear'}
        </button>
      </div>

      {error && <span className={styles.error}>{error}</span>}
      {!error && saveResult && (
        <span className={saveResult.ok ? styles.success : styles.warning}>{saveResult.message}</span>
      )}
    </Card>
  )
}
