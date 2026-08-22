import { useEffect, useState } from 'react'
import { KeyRound, UserCheck, Check, X } from 'lucide-react'
import { ApiError } from '@/lib/api/http'
import { approveTelegramRequest, denyTelegramRequest, generateTelegramClaimCode } from '@/lib/api/endpoints'
import { useTelegramPendingRequests } from '@/hooks/dashboard/useTelegramPendingRequests'
import styles from './TelegramAccessCard.module.scss'

function formatCountdown(seconds: number): string {
  const m = Math.floor(seconds / 60)
  const s = seconds % 60
  return `${m}:${s.toString().padStart(2, '0')}`
}

/**
 * Аудит-фікс (2026-08-22, п.2): дві раніше відсутні у веб-Settings дії —
 * генерація claim-коду для Primary Admin (WPF мав кнопку, веб — ні) і
 * видимість/керування pending-запитами доступу (веб взагалі не бачив
 * TelegramAccessRequestOccurred). Адмін і далі сам надсилає
 * /claim_admin <код> в самому Telegram — тут лише генерація коду.
 *
 * На відміну від WPF (лише Deny в десктопних Settings, Approve — тільки
 * inline-кнопки в самому Telegram), тут навмисно є ОБИДВІ дії — узгоджено
 * з користувачем: увесь застосунок і так за тим самим Windows AD-group
 * гейтом, що й довіра до Telegram-схвалення.
 */
export function TelegramAccessCard() {
  const { pending, isPrimaryAdminClaimed, loading, error, refetch } = useTelegramPendingRequests()

  const [claimCode, setClaimCode] = useState<string | null>(null)
  const [claimExpiresAt, setClaimExpiresAt] = useState<number | null>(null)
  const [claimError, setClaimError] = useState<string | null>(null)
  const [generating, setGenerating] = useState(false)
  const [now, setNow] = useState(() => Date.now())

  const [busyId, setBusyId] = useState<number | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)

  useEffect(() => {
    if (claimExpiresAt === null) return
    const id = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(id)
  }, [claimExpiresAt])

  const remainingSeconds = claimExpiresAt !== null ? Math.max(0, Math.round((claimExpiresAt - now) / 1000)) : 0
  const codeExpired = claimExpiresAt !== null && remainingSeconds <= 0

  const generateCode = async () => {
    setGenerating(true)
    setClaimError(null)
    try {
      const result = await generateTelegramClaimCode()
      setClaimCode(result.code)
      setClaimExpiresAt(new Date(result.expiresAt).getTime())
      setNow(Date.now())
    } catch (err) {
      setClaimError(err instanceof ApiError ? err.message : 'Unknown error.')
    } finally {
      setGenerating(false)
    }
  }

  const respond = async (id: number, action: 'approve' | 'deny') => {
    setBusyId(id)
    setActionError(null)
    try {
      await (action === 'approve' ? approveTelegramRequest(id) : denyTelegramRequest(id))
      await refetch()
    } catch (err) {
      setActionError(err instanceof ApiError ? err.message : 'Unknown error.')
    } finally {
      setBusyId(null)
    }
  }

  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>
          <KeyRound size={14} strokeWidth={1.75} />
          Telegram Access
        </span>
      </div>

      {loading ? (
        <p className={styles.hint}>Loading…</p>
      ) : (
        <>
          <div className={styles.section}>
            {isPrimaryAdminClaimed ? (
              <p className={styles.hint}>Primary Admin is already claimed.</p>
            ) : claimCode && !codeExpired ? (
              <div className={styles.claimResult}>
                <span className={styles.claimCode}>{claimCode}</span>
                <span className={styles.claimMeta}>
                  Send <code className={styles.inlineCode}>/claim_admin {claimCode}</code> to the bot within{' '}
                  {formatCountdown(remainingSeconds)} to become Primary Admin.
                </span>
              </div>
            ) : (
              <div className={styles.claimPrompt}>
                <p className={styles.hint}>No Primary Admin claimed yet — generate a one-time code and send it to the bot.</p>
                <button type="button" className={styles.generateButton} onClick={generateCode} disabled={generating}>
                  {generating ? 'Generating…' : codeExpired ? 'Generate new code' : 'Generate Claim Code'}
                </button>
              </div>
            )}
            {claimError && <p className={styles.error}>{claimError}</p>}
          </div>

          <div className={styles.divider} />

          <div className={styles.section}>
            <span className={styles.subEyebrow}>
              <UserCheck size={14} strokeWidth={1.75} />
              Pending Requests
              {pending.length > 0 && <span className={styles.count}>{pending.length}</span>}
            </span>

            {error && <p className={styles.error}>Failed to load pending requests: {error.message}</p>}
            {actionError && <p className={styles.error}>{actionError}</p>}

            {pending.length === 0 ? (
              <div className={styles.empty}>No pending requests</div>
            ) : (
              <div className={styles.pendingList}>
                {pending.map((request) => (
                  <div className={styles.pendingRow} key={request.id}>
                    <div className={styles.pendingInfo}>
                      <span className={styles.pendingUser}>@{request.username}</span>
                      <span className={styles.pendingMeta}>
                        chat_id {request.chatId} · {new Date(request.requestedAt).toLocaleString()}
                      </span>
                    </div>
                    <div className={styles.pendingActions}>
                      <button
                        type="button"
                        className={styles.approveButton}
                        onClick={() => respond(request.id, 'approve')}
                        disabled={busyId === request.id}
                        aria-label={`Approve ${request.username}`}
                      >
                        <Check size={14} strokeWidth={2} />
                      </button>
                      <button
                        type="button"
                        className={styles.denyButton}
                        onClick={() => respond(request.id, 'deny')}
                        disabled={busyId === request.id}
                        aria-label={`Deny ${request.username}`}
                      >
                        <X size={14} strokeWidth={2} />
                      </button>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>
        </>
      )}
    </div>
  )
}
