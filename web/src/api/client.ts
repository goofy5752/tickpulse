import type { Period, ProblemResponse } from './types'

// Same-origin by default: the API serves the built dashboard, and Vite proxies in development.
const API_BASE: string = import.meta.env.VITE_API_BASE_URL ?? ''

/** Resolves an API path (`/api/...`, `/hubs/...`) against the configured origin. */
export function apiUrl(path: string): string {
  return `${API_BASE}${path}`
}

/** A non-2xx answer from the API, carrying the parsed problem details when the API sent them. */
export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemResponse | undefined

  constructor(status: number, message: string, problem?: ProblemResponse) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }

  /** The problem slug, e.g. `no-data` for `https://tickpulse.dev/problems/no-data`. */
  get problemSlug(): string | undefined {
    return this.problem?.type.split('/').pop()
  }
}

/** What the API playground shows for one request: the response exactly as it arrived. */
export interface RawResponse {
  status: number
  contentType: string
  body: string
  durationMs: number
}

export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'DELETE'

export interface AdminResponse<T> {
  status: number
  data: T | undefined
}

interface RequestOptions {
  signal?: AbortSignal
}

function upper(symbol: string): string {
  return encodeURIComponent(symbol.trim().toUpperCase())
}

/** The public routes, so the playground and the queries build identical URLs. */
export const apiPaths = {
  symbols: () => '/api/symbols',
  average: (symbol: string) => `/api/${upper(symbol)}/24hAvgPrice`,
  sma: (symbol: string, n: number, p: Period, s?: string) => {
    const query = new URLSearchParams({ n: String(n), p })
    if (s) query.set('s', s)
    return `/api/${upper(symbol)}/SimpleMovingAverage?${query.toString()}`
  },
  candles: (symbol: string, p: Period, limit: number) =>
    `/api/${upper(symbol)}/candles?${new URLSearchParams({ p, limit: String(limit) }).toString()}`,
  stats: () => '/api/system/stats',
  adminSymbols: () => '/api/admin/symbols',
  adminSymbol: (symbol: string) => `/api/admin/symbols/${upper(symbol)}`,
} as const

function isProblem(value: unknown): value is ProblemResponse {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as Partial<ProblemResponse>).type === 'string' &&
    typeof (value as Partial<ProblemResponse>).status === 'number'
  )
}

async function toApiError(response: Response): Promise<ApiError> {
  const fallback = response.statusText || `Request failed with status ${response.status}`
  const contentType = response.headers.get('Content-Type') ?? ''
  if (contentType.includes('json')) {
    try {
      const body: unknown = await response.json()
      if (isProblem(body)) {
        return new ApiError(response.status, body.detail ?? body.title, body)
      }
    } catch {
      // The body was not JSON after all; fall through to the status text.
    }
  }
  return new ApiError(response.status, fallback)
}

async function readJson<T>(response: Response): Promise<T | undefined> {
  if (response.status === 204) return undefined
  const text = await response.text()
  return text.length === 0 ? undefined : (JSON.parse(text) as T)
}

/** GETs a JSON resource; throws {@link ApiError} for any non-2xx status. */
export async function getJson<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const response = await fetch(apiUrl(path), {
    headers: { Accept: 'application/json' },
    signal: options.signal ?? null,
  })
  if (!response.ok) throw await toApiError(response)
  return (await response.json()) as T
}

/**
 * GETs a resource with the given Accept header and returns it untouched, including error
 * statuses, so the playground can show content negotiation as it really happens.
 */
export async function getRaw(path: string, accept: string, options: RequestOptions = {}): Promise<RawResponse> {
  const started = performance.now()
  const response = await fetch(apiUrl(path), {
    headers: { Accept: accept },
    signal: options.signal ?? null,
  })
  const body = await response.text()
  return {
    status: response.status,
    contentType: response.headers.get('Content-Type') ?? '',
    body,
    durationMs: Math.max(0, performance.now() - started),
  }
}

/** Calls an admin endpoint with the `X-Api-Key` header; throws {@link ApiError} for any non-2xx status. */
export async function adminRequest<T = unknown>(
  method: HttpMethod,
  path: string,
  apiKey: string,
  body?: unknown,
): Promise<AdminResponse<T>> {
  const headers: Record<string, string> = { Accept: 'application/json', 'X-Api-Key': apiKey }
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  const response = await fetch(apiUrl(path), {
    method,
    headers,
    body: body === undefined ? null : JSON.stringify(body),
  })
  if (!response.ok) throw await toApiError(response)
  return { status: response.status, data: await readJson<T>(response) }
}
