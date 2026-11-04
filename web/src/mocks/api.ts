/*
 * Development-only stand-in for the TickPulse HTTP API, backed by the market simulation.
 * Installed by main.tsx only when `import.meta.env.DEV` and VITE_MOCK=1; a build never includes it.
 *
 * It negotiates JSON or XML like the real API (DataContract-style XML in the
 * https://tickpulse.dev/schema/v1 namespace, problems as application/problem+xml) and answers the
 * admin routes (list, add, get one, remove):
 * - the admin key is `dev-admin-key`; a missing or different key gets 401
 * - open the page with `?mock-admin=disabled` to get 403 `admin-disabled` on every admin call
 * - SOLUSDT, BNBUSDT, XRPUSDT, DOGEUSDT, LINKUSDT, AVAXUSDT and LTCUSDT are "trading"; any other
 *   well-formed symbol is 422 `invalid-symbol`, and the limit is MOCK_MAX_ACTIVE (409 beyond it)
 * - a new symbol's backfill goes Pending → Running → Completed over about 11 s
 */
import { isPeriod, type ProblemResponse } from '@/api/types'
import { market, MOCK_MAX_ACTIVE } from './market'

const PROBLEM_BASE = 'https://tickpulse.dev/problems/'
const SCHEMA_NS = 'https://tickpulse.dev/schema/v1'
const XSI_NS = 'http://www.w3.org/2001/XMLSchema-instance'
const MOCK_ADMIN_KEY = 'dev-admin-key'

/** How a DTO is named in XML: the root element, and the element of each item in a list. */
interface XmlShape {
  root: string
  item?: string
  /** Element names for items of nested lists, keyed by the list's (PascalCase) element. */
  lists?: Record<string, string>
}

type Reply =
  | { kind: 'data'; status: number; data: unknown; xml: XmlShape; headers?: Record<string, string> }
  | { kind: 'problem'; problem: ProblemResponse }
  | { kind: 'empty'; status: number }

type Format = 'json' | 'xml'

function data(value: unknown, xml: XmlShape, status = 200, headers?: Record<string, string>): Reply {
  return { kind: 'data', status, data: value, xml, ...(headers ? { headers } : {}) }
}

function problem(status: number, slug: string, title: string, detail: string, errors?: Record<string, string[]>): Reply {
  return {
    kind: 'problem',
    problem: { type: `${PROBLEM_BASE}${slug}`, title, status, detail, ...(errors ? { errors } : {}) },
  }
}

function unauthorized(detail: string): Reply {
  return {
    kind: 'problem',
    problem: { type: 'https://tools.ietf.org/html/rfc9110#section-15.5.2', title: 'Unauthorized', status: 401, detail },
  }
}

function unknownSymbol(symbol: string): Reply {
  return problem(404, 'unknown-symbol', 'Unknown symbol', `${symbol.toUpperCase()} is not a monitored symbol.`)
}

const SYMBOL_LIST: XmlShape = { root: 'ArrayOfSymbolResponse', item: 'SymbolResponse' }
const SYMBOL: XmlShape = { root: 'SymbolResponse' }

// ---- XML, shaped like DataContractSerializer output -------------------------------------------

function escapeXml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

const pascal = (key: string) => key.charAt(0).toUpperCase() + key.slice(1)

function xmlElement(name: string, value: unknown, lists: Record<string, string>): string {
  if (value === null || value === undefined) return `<${name} i:nil="true"/>`
  if (Array.isArray(value)) {
    const item = lists[name] ?? 'Item'
    return `<${name}>${value.map((entry) => xmlElement(item, entry, lists)).join('')}</${name}>`
  }
  if (typeof value === 'object') return `<${name}>${xmlChildren(value as Record<string, unknown>, lists)}</${name}>`
  const text = typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean' ? String(value) : ''
  return `<${name}>${escapeXml(text)}</${name}>`
}

function xmlChildren(value: Record<string, unknown>, lists: Record<string, string>): string {
  return Object.entries(value)
    .map(([key, child]) => xmlElement(pascal(key), child, lists))
    .join('')
}

function toXml(value: unknown, shape: XmlShape): string {
  const lists = shape.lists ?? {}
  const inner = Array.isArray(value)
    ? value.map((entry) => xmlElement(shape.item ?? 'Item', entry, lists)).join('')
    : xmlChildren(value as Record<string, unknown>, lists)
  return `<${shape.root} xmlns="${SCHEMA_NS}" xmlns:i="${XSI_NS}">${inner}</${shape.root}>`
}

function problemXml(p: ProblemResponse): string {
  const fields = [
    `<type>${escapeXml(p.type)}</type>`,
    `<title>${escapeXml(p.title)}</title>`,
    `<status>${p.status}</status>`,
    p.detail ? `<detail>${escapeXml(p.detail)}</detail>` : '',
  ]
  return `<problem xmlns="urn:ietf:rfc:7807">${fields.join('')}</problem>`
}

// ---- Content negotiation -----------------------------------------------------------------------

/** JSON unless XML is preferred; null when the client accepts neither (406). */
function negotiate(accept: string | null): Format | null {
  if (!accept || accept.trim() === '') return 'json'
  let json = 0
  let xml = 0
  let wildcard = 0
  for (const part of accept.split(',')) {
    const [media = '', ...params] = part.split(';').map((piece) => piece.trim().toLowerCase())
    const qParam = params.find((param) => param.startsWith('q='))
    const q = qParam ? Number(qParam.slice(2)) : 1
    if (!Number.isFinite(q)) continue
    if (media === 'application/json' || media === 'application/problem+json') json = Math.max(json, q)
    else if (media === 'application/xml' || media === 'text/xml' || media === 'application/problem+xml') xml = Math.max(xml, q)
    else if (media === '*/*' || media === 'application/*') wildcard = Math.max(wildcard, q)
  }
  const jsonQ = json || wildcard
  const xmlQ = xml || wildcard
  if (jsonQ === 0 && xmlQ === 0) return null
  return xmlQ > jsonQ ? 'xml' : 'json'
}

function respond(reply: Reply, format: Format): Response {
  if (reply.kind === 'empty') return new Response(null, { status: reply.status })
  if (reply.kind === 'problem') {
    const body = format === 'xml' ? problemXml(reply.problem) : JSON.stringify(reply.problem)
    return new Response(body, {
      status: reply.problem.status,
      headers: { 'Content-Type': `application/problem+${format}; charset=utf-8` },
    })
  }
  const body = format === 'xml' ? toXml(reply.data, reply.xml) : JSON.stringify(reply.data)
  return new Response(body, {
    status: reply.status,
    headers: { 'Content-Type': `application/${format}; charset=utf-8`, ...reply.headers },
  })
}

// ---- Routes ------------------------------------------------------------------------------------

function adminDisabled(): boolean {
  return new URLSearchParams(window.location.search).get('mock-admin') === 'disabled'
}

function readSymbol(body: unknown): string | null {
  if (typeof body !== 'string') return null
  try {
    const parsed: unknown = JSON.parse(body)
    const symbol = (parsed as { symbol?: unknown } | null)?.symbol
    return typeof symbol === 'string' && symbol.trim() !== '' ? symbol : null
  } catch {
    return null
  }
}

function adminRoute(url: URL, method: string, headers: Headers, body: unknown): Reply {
  if (adminDisabled()) {
    return problem(403, 'admin-disabled', 'Admin disabled', 'Symbol management is disabled because no admin API key is configured.')
  }
  const key = headers.get('X-Api-Key')
  if (!key) return unauthorized('The X-Api-Key header is missing.')
  if (key !== MOCK_ADMIN_KEY) return unauthorized('The API key is not valid.')

  if (url.pathname === '/api/admin/symbols') {
    if (method === 'GET') return data(market.symbols(), SYMBOL_LIST)
    if (method !== 'POST') return problem(405, 'validation', 'Method not allowed', `${method} is not supported here.`)
    const raw = readSymbol(body)
    if (raw === null) {
      return problem(400, 'validation', 'Validation failed', 'symbol is required.', {
        symbol: ['symbol is required.'],
      })
    }
    const outcome = market.addSymbol(raw)
    switch (outcome.kind) {
      case 'invalid-format':
        return problem(422, 'invalid-symbol', 'Invalid symbol', `'${outcome.symbol}' is not a valid symbol: use 5 to 20 letters or digits.`)
      case 'not-trading':
        return problem(422, 'invalid-symbol', 'Invalid symbol', `${outcome.symbol} is not a trading symbol on Binance.`)
      case 'limit':
        return problem(409, 'symbol-limit', 'Symbol limit reached', `At most ${outcome.max} symbols can be active at once.`)
      case 'already':
        return data(outcome.symbol, SYMBOL)
      case 'created':
        return data(outcome.symbol, SYMBOL, 201, { Location: `/api/admin/symbols/${outcome.symbol.symbol}` })
    }
  }

  const match = /^\/api\/admin\/symbols\/([^/]+)$/.exec(url.pathname)
  if (match && method === 'GET') {
    // The target of the Location header of a created symbol; inactive symbols are found too.
    const symbol = decodeURIComponent(match[1] ?? '')
    const found = market.symbols().find((entry) => entry.symbol === symbol.trim().toUpperCase())
    return found ? data(found, SYMBOL) : unknownSymbol(symbol)
  }
  if (match && method === 'DELETE') {
    const symbol = decodeURIComponent(match[1] ?? '')
    return market.removeSymbol(symbol) ? { kind: 'empty', status: 204 } : unknownSymbol(symbol)
  }
  return problem(404, 'internal', 'Not found', `No mock route for ${method} ${url.pathname}.`)
}

function route(url: URL, method: string, headers: Headers, body: unknown): Reply {
  if (url.pathname.startsWith('/api/admin/')) return adminRoute(url, method, headers, body)
  if (method !== 'GET') {
    return problem(501, 'internal', 'Not available', 'The mock API only answers GET requests outside the admin routes.')
  }
  const path = url.pathname
  if (path === '/api/symbols') return data(market.symbols(), SYMBOL_LIST)
  if (path === '/api/system/stats') return data(market.stats(), { root: 'SystemStatsResponse' })

  const match = /^\/api\/([^/]+)\/(24hAvgPrice|SimpleMovingAverage|candles)$/.exec(path)
  if (!match) return problem(404, 'internal', 'Not found', `No mock route for ${path}.`)
  const symbol = decodeURIComponent(match[1] ?? '')
  const endpoint = match[2]
  if (!market.has(symbol)) return unknownSymbol(symbol)

  if (endpoint === '24hAvgPrice') {
    const average = market.average(symbol)
    return average
      ? data(average, { root: 'AveragePriceResponse' })
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
    return data(market.candles(symbol, p, limit), { root: 'ArrayOfCandleResponse', item: 'CandleResponse' })
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
  return data(result, { root: 'SmaResponse', lists: { DataPoints: 'SmaDataPoint' } })
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
    const method = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase()
    const headers = new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined))
    const format = negotiate(headers.get('Accept'))
    if (format === null) {
      return respond(
        problem(406, 'not-acceptable', 'Not acceptable', 'The API answers in application/json or application/xml.'),
        'json',
      )
    }
    return respond(route(url, method, headers, init?.body), format)
  }
  console.info(
    `TickPulse: mock API on (VITE_MOCK=1). Prices are simulated, not live. Admin key: ${MOCK_ADMIN_KEY}, symbol limit ${MOCK_MAX_ACTIVE}.`,
  )
}
