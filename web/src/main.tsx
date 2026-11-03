import { QueryClientProvider } from '@tanstack/react-query'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { createQueryClient } from '@/api/queries'
import { App } from './App'
import './theme.css'

const root = document.getElementById('root')
if (!root) throw new Error('The #root element is missing from index.html')

const queryClient = createQueryClient()

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <App />
    </QueryClientProvider>
  </StrictMode>,
)
