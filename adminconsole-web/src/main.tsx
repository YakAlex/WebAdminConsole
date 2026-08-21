import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import { AuthProvider } from '@/lib/auth/AuthContext'
import { DashboardConnectionProvider } from '@/lib/signalr/DashboardConnectionContext'
import '@/styles/index.scss'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AuthProvider>
      <DashboardConnectionProvider>
        <App />
      </DashboardConnectionProvider>
    </AuthProvider>
  </StrictMode>,
)
