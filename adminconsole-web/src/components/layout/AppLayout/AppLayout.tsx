import { Outlet } from 'react-router-dom'
import { Sidebar } from '../Sidebar/Sidebar'
import { TopBar } from '../TopBar/TopBar'
import styles from './AppLayout.module.scss'

/**
 * Композиція 4-зонного каркасу (§2 брифу): Sidebar зліва на всю висоту,
 * TopBar зверху правої колонки, під ним — скролований, відцентрований
 * контент сторінки (React Router <Outlet /> — самі сторінки поки заглушки).
 */
export function AppLayout() {
  return (
    <div className={styles.root}>
      <Sidebar />
      <div className={styles.main}>
        <TopBar />
        <div className={styles.content}>
          <div className={styles.contentInner}>
            <Outlet />
          </div>
        </div>
      </div>
    </div>
  )
}
