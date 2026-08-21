import { NavLink } from 'react-router-dom'
import { Diamond, Settings } from 'lucide-react'
import clsx from 'clsx'
import { navItems } from './navItems'
import styles from './Sidebar.module.scss'

/**
 * Фіксована ліва навігаційна панель (§4 брифу). Design-system правило:
 * усі 8 сторінок з'являться тут у Фазі 6 — сам Sidebar більше не міняється,
 * лише роутинг під капотом (React Router NavLink керує active-станом).
 */
export function Sidebar() {
  return (
    <aside className={styles.sidebar}>
      <div className={styles.logo}>
        <Diamond size={20} strokeWidth={2} className={styles.logoMark} />
        <span className={styles.logoWordmark}>ADMIN CONSOLE</span>
      </div>

      <nav className={styles.nav} aria-label="Основна навігація">
        {navItems.map(({ path, label, icon: Icon }) => (
          <NavLink
            key={path}
            to={path}
            end={path === '/'}
            className={({ isActive }) => clsx(styles.navItem, isActive && styles.navItemActive)}
          >
            <Icon size={18} strokeWidth={1.75} />
            <span>{label}</span>
          </NavLink>
        ))}
      </nav>

      <div className={styles.footer}>
        <NavLink
          to="/settings"
          className={({ isActive }) => clsx(styles.navItem, isActive && styles.navItemActive)}
        >
          <Settings size={18} strokeWidth={1.75} />
          <span>Settings</span>
        </NavLink>

        <div className={styles.healthIndicator}>
          <span className={styles.healthDot} aria-hidden="true" />
          <span>
            All systems
            <br />
            operational
          </span>
        </div>
      </div>
    </aside>
  )
}
