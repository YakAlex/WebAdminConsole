import { useState } from 'react'
import { Users, User, Trash2 } from 'lucide-react'
import { ApiError } from '@/lib/api/http'
import type { TelegramAllowedUserView } from '@/lib/api/types'
import styles from './TelegramUsersTable.module.scss'

export interface TelegramUsersTableProps {
  users: TelegramAllowedUserView[]
  onRemove: (chatId: number) => Promise<void>
}

/**
 * §T6.2 п.3 (Settings → Telegram Users) + аудит-фікс п.5: раніше тут була
 * ручна форма Chat ID/Username/"Add" — але реальна авторизація нових
 * користувачів іде виключно через сам бот (/start → запит → Primary Admin
 * підтверджує ✅/❌ прямо в Telegram, TelegramAccessControlService/
 * TelegramBotService). Ручне додавання тут дублювало інший, не пов'язаний
 * з тим флоу шлях і могло ввести в оману, що це "офіційний" спосіб додати
 * когось. Лишається лише список уже підтверджених користувачів + видалення.
 */
export function TelegramUsersTable({ users, onRemove }: TelegramUsersTableProps) {
  const [removingChatId, setRemovingChatId] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)

  const handleRemove = async (id: number) => {
    setRemovingChatId(id)
    setError(null)
    try {
      await onRemove(id)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не вдалося видалити користувача.')
    } finally {
      setRemovingChatId(null)
    }
  }

  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>
          <Users size={14} strokeWidth={1.75} />
          Telegram Users
        </span>
      </div>

      <p className={styles.hint}>
        New users authorize themselves via the bot (/start) — the Primary Admin approves or denies the request directly in
        Telegram.
      </p>

      {error && <div className={styles.error}>{error}</div>}

      {users.length === 0 ? (
        <div className={styles.empty}>No allowed users yet</div>
      ) : (
        <div className={styles.tableWrap}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th>Username</th>
                <th>Chat ID</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {users.map((user) => (
                <tr key={user.chatId}>
                  <td>
                    <span className={styles.user}>
                      <User size={14} strokeWidth={1.75} className={styles.userIcon} />
                      {user.username}
                    </span>
                  </td>
                  <td className={styles.chatId}>{user.chatId}</td>
                  <td className={styles.removeCell}>
                    <button
                      type="button"
                      className={styles.removeButton}
                      onClick={() => handleRemove(user.chatId)}
                      disabled={removingChatId === user.chatId}
                      aria-label={`Remove ${user.username}`}
                    >
                      <Trash2 size={14} strokeWidth={1.75} />
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
