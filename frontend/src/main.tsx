import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { Toaster } from 'react-hot-toast'
import App from '@/App'
import DiagnosticBoundary from '@/components/DiagnosticBoundary'
import { installGlobalDiagnostics, reportUiFailure } from '@/lib/diagnostics'
import '@/index.css'
import '@/styles/panel.css'
import '@/styles/server-workspace.css'

const removeDiagnostics = installGlobalDiagnostics()
if (import.meta.hot) import.meta.hot.dispose(removeDiagnostics)

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      retry: 1,
      staleTime: 30000,
    },
  },
})

async function mount() {
  if (import.meta.env.DEV && import.meta.env.VITE_DEMO_MODE === 'true') {
    const { enableDemo } = await import('@/demo/preview')
    enableDemo()
  }

  ReactDOM.createRoot(document.getElementById('root')!).render(
    <React.StrictMode>
      <DiagnosticBoundary>
        <QueryClientProvider client={queryClient}>
          <BrowserRouter>
            <App />
            <Toaster
              position="top-right"
              toastOptions={{
                className: '',
                style: {
                  background: '#1e293b',
                  color: '#f1f5f9',
                  border: '1px solid #334155',
                },
              }}
            />
          </BrowserRouter>
        </QueryClientProvider>
      </DiagnosticBoundary>
    </React.StrictMode>
  )
}

void mount().catch(() => reportUiFailure('ANUI005'))
