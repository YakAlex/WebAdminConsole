import { useState } from 'react'
import { Users, User, Plus, Trash2 } from 'lucide-react'
import { ApiError } from '@/lib/api/http'
import type { TelegramAllowedUserView } from '@/lib/api/types'
import styles from './TelegramUsersTable.module.scss'

export interface TelegramUsersTableProps {
  users: TelegramAllowedUserView[]
  onAdd: (chatId: number, username: string) => Promise<void>
  onRemove: (chatId: number) => Promise<void>
}

/** §T6.2 п.3 (Settings → Telegram Users): таблиця дозволених користувачів + форма додавання/видалення. */
export function TelegramUsersTable({ users, onAdd, onRemove }: TelegramUsersTableProps) {
  const [chatId, setChatId] = useState('')
  const [username, setUsername] = useState('')
  const [adding, setAdding] = useState(false)
  const [removingChatId, setRemovingChatId] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)

  const parsedChatId = Number(chatId)
  const canAdd = chatId.trim().length > 0 && Number.isFinite(parsedChatId) && parsedChatId !== 0

  const handleAdd = async () => {
    if (!canAdd) return
    setAdding(true)
    setError(null)
    try {
      await onAdd(parsedChatId, username.trim())
      setChatId('')
      setUsername('')
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Не вдалося додати користувача.')
    } finally {
      setAdding(false)
    }
  }

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

      <div className={styles.addForm}>
        <input
          className={`${styles.input} ${styles.chatIdInput}`}
          type="text"
          inputMode="numeric"
          placeholder="Chat ID"
          value={chatId}
          onChange={(e) => setChatId(e.target.value)}
          disabled={adding}
        />
        <input
          className={`${styles.input} ${styles.usernameInput}`}
          type="text"
          placeholder="Username (optional)"
          value={username}
          onChange={(e) => setUsername(e.target.value)}
          disabled={adding}
        />
        <button type="button" className={styles.addButton} onClick={handleAdd} disabled={adding || !canAdd}>
          <Plus size={14} strokeWidth={2} />
          {adding ? 'Adding…' : 'Add'}
        </button>
      </div>

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
