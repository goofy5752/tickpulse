import { fileURLToPath, URL } from 'node:url'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { loadEnv } from 'vite'
import { defineConfig } from 'vitest/config'

// The API (and the SignalR hub) listens on :8080 in development; Vite proxies to it so the
// dashboard uses same-origin relative URLs everywhere, exactly as it does when the API serves it.
const apiTarget = 'http://localhost:8080'

const src = (path: string) => fileURLToPath(new URL(`./src/${path}`, import.meta.url))

export default defineConfig(({ command, mode }) => {
  // `npm run dev:mock` (or VITE_MOCK=1 npm run dev) runs the dashboard without a backend: main.tsx
  // installs a simulated API and the hub client is swapped for a simulated one. Only `serve`
  // outside tests does this, so no build or test run ever sees the simulation.
  const mock = command === 'serve' && mode !== 'test' && loadEnv(mode, process.cwd(), 'VITE_').VITE_MOCK === '1'

  return {
    plugins: [react(), tailwindcss()],
    resolve: {
      alias: [
        ...(mock ? [{ find: /^@microsoft\/signalr$/, replacement: src('mocks/signalr.ts') }] : []),
        { find: '@', replacement: src('') },
      ],
    },
    server: {
      proxy: {
        '/api': apiTarget,
        '/hubs': { target: apiTarget, ws: true },
        '/health': apiTarget,
        '/docs': apiTarget,
        '/openapi': apiTarget,
      },
    },
    build: {
      outDir: 'dist',
      sourcemap: true,
    },
    test: {
      environment: 'jsdom',
      include: ['src/**/*.test.{ts,tsx}'],
      restoreMocks: true,
      clearMocks: true,
    },
  }
})
