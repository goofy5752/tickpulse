import { QueryClientProvider } from '@tanstack/react-query'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { createQueryClient } from '@/api/queries'
import { App } from './App'
import './theme.css'

const root = document.getElementById('root')
if (!root) throw new Error('The #root element is missing from index.html')

const queryClient = createQueryClient()

async function start(container: HTMLElement) {
  // `npm run dev:mock` serves simulated market data. DEV is false in every build, so the bundler
  // drops this branch and the mock modules with it.
  if (import.meta.env.DEV && import.meta.env.VITE_MOCK === '1') {
    const { installMockApi } = await import('./mocks/api')
    installMockApi()
  }

  createRoot(container).render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <App />
      </QueryClientProvider>
    </StrictMode>,
  )
}

void start(root)
