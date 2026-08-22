import { Outlet } from 'react-router-dom'
import { Sidebar } from '../Sidebar/Sidebar'
import { TopBar } from '../TopBar/TopBar'
import styles from './AppLayout.module.scss'

/**
 * Composition of the 4-zone frame (brief §2): Sidebar on the left at full
 * height, TopBar at the top of the right column, below it — scrollable
 * page content (React Router <Outlet /> — the pages themselves are still
 * placeholders for now).
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
