import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import { AuthProvider } from '@/lib/auth/AuthContext'
import { DashboardConnectionProvider } from '@/lib/signalr/DashboardConnectionContext'
import { ErrorBoundary } from '@/components/ui/ErrorBoundary'
import '@/styles/index.scss'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <AuthProvider>
        <DashboardConnectionProvider>
          <App />
        </DashboardConnectionProvider>
      </AuthProvider>
    </ErrorBoundary>
  </StrictMode>,
)
