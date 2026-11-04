/*
 * Development-only stand-in for the TickPulse HTTP API, backed by the market simulation.
 * Installed by main.tsx only when `import.meta.env.DEV` and VITE_MOCK=1; a build never includes it.
 */
import { isPeriod, type ProblemResponse } from '@/api/types'
import { market } from './market'

const PROBLEM_BASE = 'https://tickpulse.dev/problems/'

function json(body: unknown, status = 200): Response {
  return Response.json(body, { status })
}

function problem(status: number, slug: string, title: string, detail: string, errors?: Record<string, string[]>): Response {
  const body: ProblemResponse = { type: `${PROBLEM_BASE}${slug}`, title, status, detail, ...(errors ? { errors } : {}) }
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/problem+json' } })
}

function unknownSymbol(symbol: string): Response {
  return problem(404, 'unknown-symbol', 'Unknown symbol', `${symbol.toUpperCase()} is not a monitored symbol.`)
}

function route(url: URL, method: string): Response {
  if (method !== 'GET') {
    return problem(501, 'internal', 'Not available', 'The mock API only answers GET requests.')
  }
  const path = url.pathname
  if (path === '/api/symbols') return json(market.symbols())
  if (path === '/api/system/stats') return json(market.stats())

  const match = /^\/api\/([^/]+)\/(24hAvgPrice|SimpleMovingAverage|candles)$/.exec(path)
  if (!match) return problem(404, 'internal', 'Not found', `No mock route for ${path}.`)
  const symbol = decodeURIComponent(match[1] ?? '')
  const endpoint = match[2]
  if (!market.has(symbol)) return unknownSymbol(symbol)

  if (endpoint === '24hAvgPrice') {
    const average = market.average(symbol)
    return average
      ? json(average)
      : problem(404, 'no-data', 'No data', `No trades for ${symbol.toUpperCase()} in the last 24 hours.`)
  }

  const p = url.searchParams.get('p') ?? (endpoint === 'candles' ? '1m' : '')
  if (!isPeriod(p)) {
    return problem(400, 'validation', 'Validation failed', 'p must be one of 1w, 1d, 30m, 5m, 1m.', {
      p: ['Must be one of 1w, 1d, 30m, 5m, 1m.'],
    })
  }

  if (endpoint === 'candles') {
    const limit = Number(url.searchParams.get('limit') ?? '300')
    if (!Number.isInteger(limit) || limit < 1 || limit > 1000) {
      return problem(400, 'validation', 'Validation failed', 'limit must be between 1 and 1000.', {
        limit: ['Must be between 1 and 1000.'],
      })
    }
    return json(market.candles(symbol, p, limit))
  }

  const nText = url.searchParams.get('n') ?? ''
  const n = Number(nText)
  if (!/^\d+$/.test(nText) || n < 1 || n > 1000) {
    return problem(400, 'validation', 'Validation failed', 'n must be between 1 and 1000.', {
      n: ['Must be between 1 and 1000.'],
    })
  }
  const sText = url.searchParams.get('s')
  const s = sText ? Date.parse(sText) : undefined
  if (s !== undefined && Number.isNaN(s)) {
    return problem(400, 'validation', 'Validation failed', 's must be an ISO-8601 timestamp.', {
      s: ['Must be an ISO-8601 timestamp.'],
    })
  }
  const result = market.sma(symbol, n, p, s === undefined ? undefined : Math.floor(s / 1000))
  if (result === 'no-data' || result === null) {
    return problem(404, 'no-data', 'No data', `No ${p} closes for ${symbol.toUpperCase()} in the requested window.`)
  }
  return json(result)
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input
  return input instanceof URL ? input.href : input.url
}

/** Answers same-origin `/api/*` requests from the simulation, with a realistic delay. */
export function installMockApi() {
  const realFetch = window.fetch.bind(window)
  window.fetch = async (input, init) => {
    const url = new URL(urlOf(input), window.location.origin)
    if (url.origin !== window.location.origin || !url.pathname.startsWith('/api/')) return realFetch(input, init)
    await new Promise((resolve) => setTimeout(resolve, 80 + Math.random() * 160))
    const method = init?.method ?? (input instanceof Request ? input.method : 'GET')
    return route(url, method.toUpperCase())
  }
  console.info('TickPulse: mock API on (VITE_MOCK=1). Prices are simulated, not live.')
}
