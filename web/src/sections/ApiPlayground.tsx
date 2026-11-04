import { useMutation } from '@tanstack/react-query'
import { Fragment, memo, useEffect, useId, useMemo, useRef, useState, type SyntheticEvent } from 'react'
import { apiPaths, apiUrl, getRaw, type RawResponse } from '@/api/client'
import type { Period } from '@/api/types'
import { FieldLabel } from '@/components/FieldLabel'
import { InfoTip } from '@/components/InfoTip'
import { Segmented, type SegmentedOption } from '@/components/Segmented'
import { Select } from '@/components/Select'
import { SuggestInput } from '@/components/SuggestInput'
import { highlightBody, type TokenKind } from '@/lib/highlight'
import { SECTION_IDS } from '@/lib/links'
import { PERIOD_OPTIONS } from '@/lib/periodOptions'
import { control, primaryButton, sectionLead, sectionTitle } from '@/lib/styles'
import { cn } from '@/lib/utils'

type Endpoint = 'average' | 'sma' | 'symbols' | 'candles'
type Accept = 'application/json' | 'application/xml'

const ENDPOINTS: readonly SegmentedOption<Endpoint>[] = [
  { value: 'average', label: '24h average' },
  { value: 'sma', label: 'Moving average' },
  { value: 'candles', label: 'Candles' },
  { value: 'symbols', label: 'Symbols' },
]

const ACCEPTS: readonly SegmentedOption<Accept>[] = [
  { value: 'application/json', label: 'application/json' },
  { value: 'application/xml', label: 'application/xml' },
]

const REASONS: Record<number, string> = {
  200: 'OK',
  201: 'Created',
  204: 'No Content',
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  406: 'Not Acceptable',
  409: 'Conflict',
  422: 'Unprocessable Content',
  429: 'Too Many Requests',
  500: 'Internal Server Error',
  502: 'Bad Gateway',
  503: 'Service Unavailable',
}

const TOKEN_CLASS: Record<TokenKind, string> = {
  punct: 'text-ink-faint',
  name: 'text-code-name',
  string: 'text-code-string',
  value: 'text-ink',
  attr: 'text-ink-muted',
  meta: 'text-ink-faint italic',
  plain: 'text-ink',
}

interface Fields {
  symbol: string
  n: string
  smaP: Period
  s: string
  candlesP: Period
  limit: string
}

/**
 * The request as the API will see it. Field text goes in as typed (only the symbol is
 * upper-cased, as the API does), so a bad value earns the API's own validation answer.
 */
function requestPath(endpoint: Endpoint, f: Fields): string {
  const symbol = encodeURIComponent(f.symbol.trim().toUpperCase())
  switch (endpoint) {
    case 'symbols':
      return apiPaths.symbols()
    case 'average':
      return apiPaths.average(f.symbol)
    case 'sma': {
      const query = new URLSearchParams({ n: f.n.trim(), p: f.smaP })
      if (f.s.trim()) query.set('s', f.s.trim())
      return `/api/${symbol}/SimpleMovingAverage?${query.toString()}`
    }
    case 'candles':
      return `/api/${symbol}/candles?${new URLSearchParams({ p: f.candlesP, limit: f.limit.trim() }).toString()}`
  }
}

/**
 * POSIX single quotes: nothing inside is expanded (no `!` history, `$`, backticks), and a literal
 * `'` (which encodeURIComponent leaves alone) is closed, escaped and reopened.
 */
function shellQuote(text: string): string {
  return `'${text.replace(/'/g, `'\\''`)}'`
}

function absolute(path: string): string {
  return new URL(apiUrl(path), window.location.origin).href
}

interface Sent extends RawResponse {
  path: string
  accept: Accept
}

interface ApiPlaygroundProps {
  /** The symbol on the chart; the playground starts from it until someone types another. */
  symbol: string | undefined
  symbols: readonly string[]
}

/**
 * Real requests to the public API, shown as they go out (URL, curl) and as they come back
 * (status, content type, timing and the raw body, pretty-printed but never re-serialised).
 */
export const ApiPlayground = memo(function ApiPlayground({ symbol, symbols }: ApiPlaygroundProps) {
  const ids = useId()
  const [endpoint, setEndpoint] = useState<Endpoint>('average')
  const [accept, setAccept] = useState<Accept>('application/json')
  const [typedSymbol, setTypedSymbol] = useState<string | null>(null)
  const [n, setN] = useState('20')
  const [smaP, setSmaP] = useState<Period>('1d')
  const [s, setS] = useState('')
  const [candlesP, setCandlesP] = useState<Period>('5m')
  const [limit, setLimit] = useState('5')

  const symbolText = typedSymbol ?? symbol ?? ''
  const needsSymbol = endpoint !== 'symbols'
  const canSend = !needsSymbol || symbolText.trim().length > 0
  const path = requestPath(endpoint, { symbol: symbolText, n, smaP, s, candlesP, limit })
  const url = absolute(path)
  const curl = `curl -H "Accept: ${accept}" ${shellQuote(url)}`

  const request = useMutation({
    mutationFn: async (next: { path: string; accept: Accept }): Promise<Sent> => ({
      ...(await getRaw(next.path, next.accept)),
      ...next,
    }),
  })
  const { mutate } = request

  // One request as soon as there is a symbol, so the panel opens on a real answer.
  const autoSent = useRef(false)
  useEffect(() => {
    if (autoSent.current || !canSend) return
    autoSent.current = true
    mutate({ path, accept })
  }, [canSend, path, accept, mutate])

  function send(event?: SyntheticEvent<HTMLFormElement>) {
    event?.preventDefault()
    if (canSend) mutate({ path, accept })
  }

  function changeAccept(next: Accept) {
    setAccept(next)
    // Comparing JSON with XML is the point of the toggle: re-send right away once there is an answer.
    if (canSend && !request.isIdle) mutate({ path, accept: next })
  }

  return (
    <section id={SECTION_IDS.playground} aria-labelledby={`${ids}-title`} className="glass p-5 sm:p-6">
      <div className="flex flex-wrap items-end justify-between gap-x-6 gap-y-3">
        <div>
          <h2 id={`${ids}-title`} className={sectionTitle}>
            API playground
          </h2>
          <p className={sectionLead}>Send a real request to the public API and read the answer exactly as it arrives.</p>
        </div>
        <div className="flex min-w-0 items-center gap-2">
          <Segmented legend="Accept header" visibleLabel="Accept" mono options={ACCEPTS} value={accept} onChange={changeAccept} />
        </div>
      </div>

      <div className="mt-5 grid grid-cols-1 gap-5 lg:grid-cols-[minmax(0,5fr)_minmax(0,7fr)] lg:gap-6">
        <form onSubmit={send} className="flex min-w-0 flex-col gap-4" aria-label="Request">
          <div className="flex min-w-0 items-start gap-2">
            <Segmented legend="Endpoint" options={ENDPOINTS} value={endpoint} onChange={setEndpoint} />
          </div>

          <div className="grid grid-cols-2 gap-2.5 sm:grid-cols-4 lg:grid-cols-2 xl:grid-cols-4">
            {needsSymbol && (
              <div className={cn(endpoint === 'average' ? 'col-span-2' : 'col-span-2 sm:col-span-1 lg:col-span-2 xl:col-span-1')}>
                <FieldLabel htmlFor={`${ids}-symbol`}>
                  Symbol
                </FieldLabel>
                <SuggestInput
                  id={`${ids}-symbol`}
                  value={symbolText}
                  onChange={setTypedSymbol}
                  suggestions={symbols}
                  autoComplete="off"
                  spellCheck={false}
                  autoCapitalize="characters"
                  className={cn(control, 'font-mono text-[0.8125rem] uppercase')}
                />
              </div>
            )}
            {endpoint === 'sma' && (
              <>
                <TextField id={`${ids}-n`} label="n" value={n} onChange={setN} numeric />
                <PeriodField id={`${ids}-p`} label="p" value={smaP} onChange={setSmaP} />
                <div className="col-span-2 sm:col-span-4 lg:col-span-2 xl:col-span-4">
                  <TextField
                    id={`${ids}-s`}
                    label="s (optional, ISO-8601)"
                    value={s}
                    onChange={setS}
                    placeholder="2026-10-01T00:00:00Z"
                  />
                </div>
              </>
            )}
            {endpoint === 'candles' && (
              <>
                <PeriodField id={`${ids}-cp`} label="p" value={candlesP} onChange={setCandlesP} />
                <TextField id={`${ids}-limit`} label="limit" value={limit} onChange={setLimit} numeric />
              </>
            )}
          </div>

          <div className="min-w-0">
            <FieldLabel>Request</FieldLabel>
            <p className="font-mono text-[0.75rem] leading-relaxed [overflow-wrap:anywhere] text-ink">
              <span className="mr-2 rounded-[0.3rem] bg-up-soft px-1.5 py-0.5 font-medium text-up">GET</span>
              {url}
            </p>
          </div>

          <div className="min-w-0">
            <div className="mb-1.5 flex items-center justify-between gap-2">
              <span className="flex items-center gap-1.5">
                <span className="text-[0.75rem] font-medium text-ink-muted">curl</span>
                
              </span>
              <CopyButton text={curl} />
            </div>
            <pre className="rounded-(--radius-control) border border-glass-border bg-glass-strong px-3 py-2.5 text-[0.75rem] leading-relaxed [overflow-wrap:anywhere] whitespace-pre-wrap text-ink">
              <code>{curl}</code>
            </pre>
          </div>

          <div className="flex items-center gap-3">
            <button type="submit" disabled={!canSend} className={primaryButton}>
              Send request
            </button>
            {!canSend && <span className="text-[0.75rem] text-ink-faint">Enter a symbol first.</span>}
          </div>
        </form>

        <ResponseView
          result={request.data}
          error={request.error}
          pending={request.isPending}
          stale={request.data !== undefined && (request.data.path !== path || request.data.accept !== accept)}
        />
      </div>
    </section>
  )
})

function TextField({
  id,
  label,
  value,
  onChange,
  numeric = false,
  placeholder,
}: {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  numeric?: boolean
  placeholder?: string
}) {
  return (
    <div>
      <FieldLabel htmlFor={id}>
        {label}
      </FieldLabel>
      <input
        id={id}
        value={value}
        onChange={(event) => {
          onChange(event.target.value)
        }}
        inputMode={numeric ? 'numeric' : undefined}
        placeholder={placeholder}
        autoComplete="off"
        spellCheck={false}
        className={cn(control, 'font-mono text-[0.8125rem] placeholder:text-ink-faint')}
      />
    </div>
  )
}

function PeriodField({
  id,
  label,
  value,
  onChange,
}: {
  id: string
  label: string
  value: Period
  onChange: (p: Period) => void
}) {
  return (
    <div>
      <FieldLabel htmlFor={id}>
        {label}
      </FieldLabel>
      <Select id={id} value={value} options={PERIOD_OPTIONS} onChange={onChange} mono />
    </div>
  )
}

function CopyButton({ text }: { text: string }) {
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle')
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined)

  useEffect(
    () => () => {
      clearTimeout(timer.current)
    },
    [],
  )

  async function copy() {
    try {
      await navigator.clipboard.writeText(text)
      setState('copied')
    } catch {
      setState('failed')
    }
    clearTimeout(timer.current)
    timer.current = setTimeout(() => {
      setState('idle')
    }, 1800)
  }

  return (
    <span className="flex items-center gap-2">
      <span aria-live="polite" className="text-[0.75rem] text-ink-faint">
        {state === 'copied' ? 'Copied' : state === 'failed' ? 'Copy blocked, select the text instead' : ''}
      </span>
      <button
        type="button"
        onClick={() => {
          void copy()
        }}
        className="rounded-full px-2 py-0.5 text-[0.75rem] font-medium text-ink-muted transition-colors duration-150 hover:bg-glass-strong hover:text-ink"
      >
        Copy<span className="sr-only"> curl command</span>
      </button>
    </span>
  )
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  return `${(bytes / 1024).toFixed(bytes < 10_240 ? 1 : 0)} KB`
}

interface ResponseViewProps {
  result: Sent | undefined
  error: Error | null
  pending: boolean
  stale: boolean
}

/** The answer laid out like an HTTP message: status line, headers, then the body. */
function ResponseView({ result, error, pending, stale }: ResponseViewProps) {
  const lines = useMemo(() => (result ? highlightBody(result.body, result.contentType) : []), [result])
  const size = useMemo(() => (result ? new TextEncoder().encode(result.body).length : 0), [result])
  const ok = result !== undefined && result.status >= 200 && result.status < 300

  return (
    <div
      className="flex min-h-[18rem] min-w-0 flex-col overflow-hidden rounded-(--radius-control) border border-glass-border bg-[color-mix(in_oklab,var(--color-bg)_62%,transparent)]"
      aria-busy={pending}
    >
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-glass-border px-4 py-3">
        <h3 className="sr-only">Response</h3>
        {result ? (
          <>
            <p className="flex items-center gap-2 font-mono text-[0.75rem]">
              <span className="text-ink-faint">HTTP</span>
              <span
                className={cn(
                  'rounded-[0.3rem] px-1.5 py-0.5 font-medium',
                  ok ? 'bg-up-soft text-up' : 'bg-down-soft text-down',
                )}
              >
                {`${result.status} ${REASONS[result.status] ?? ''}`.trim()}
              </span>
              <InfoTip topic="playStatus" />
            </p>
            <dl className="ml-auto flex items-center gap-4 font-mono text-[0.75rem]">
              <div className="flex gap-1.5">
                <dt className="flex items-center gap-1 text-ink-faint">
                  time
                </dt>
                <dd className="num text-ink">{`${Math.round(result.durationMs)} ms`}</dd>
              </div>
              <div className="flex gap-1.5">
                <dt className="flex items-center gap-1 text-ink-faint">
                  size
                </dt>
                <dd className="num text-ink">{formatBytes(size)}</dd>
              </div>
            </dl>
            <div className="flex w-full min-w-0 items-center gap-1.5 font-mono text-[0.75rem]">
              <dl className="min-w-0">
                <div className="flex min-w-0 gap-1.5">
                  <dt className="shrink-0 text-code-name">Content-Type:</dt>
                  <dd className="min-w-0 truncate text-ink" title={result.contentType}>
                    {result.contentType || '(none)'}
                  </dd>
                </div>
              </dl>
              <span className="ml-auto flex shrink-0 items-center gap-1 pl-3 text-ink-faint">
                body
              </span>
            </div>
          </>
        ) : (
          <p className="font-mono text-[0.75rem] text-ink-faint">{pending ? 'Sending…' : 'No response yet'}</p>
        )}
        {pending && result && (
          <span className="flex items-center gap-1.5 text-[0.75rem] text-ink-muted">
            <span aria-hidden="true" className="status-dot-wait relative size-1.5 rounded-full bg-ink-faint" />
            Sending
          </span>
        )}
      </div>

      {stale && !pending && (
        <p className="border-b border-glass-border px-4 py-2 text-[0.75rem] text-ink-faint">
          This answer is for the previous request. Send again to refresh it.
        </p>
      )}

      {error && !pending ? (
        <div role="alert" className="flex flex-1 flex-col items-center justify-center gap-1 p-6 text-center">
          <p className="font-medium text-ink">The API did not answer.</p>
          <p className="text-[0.8125rem] text-ink-muted">Check that the API is running, then send again.</p>
          <p className="font-mono text-[0.75rem] text-ink-faint">{error.message}</p>
        </div>
      ) : result ? (
        <div
          role="region"
          aria-label="Response body"
          tabIndex={0}
          className={cn(
            'max-h-[28rem] flex-1 overflow-auto px-4 py-3 transition-opacity duration-200',
            (pending || stale) && 'opacity-55',
          )}
        >
          {lines.length === 0 ? (
            <p className="font-mono text-[0.75rem] text-ink-faint">(empty body)</p>
          ) : (
            <pre className="w-max min-w-full text-[0.75rem] leading-[1.7]">
              <code>
                {lines.map((line, i) => (
                  <Fragment key={i}>
                    {i > 0 && '\n'}
                    {'  '.repeat(line.indent)}
                    {line.tokens.map((token, j) => (
                      <span key={j} className={TOKEN_CLASS[token.kind]}>
                        {token.text}
                      </span>
                    ))}
                  </Fragment>
                ))}
              </code>
            </pre>
          )}
        </div>
      ) : (
        <div className="flex flex-1 items-center justify-center p-6 text-center text-[0.8125rem] text-ink-faint">
          {pending ? 'Waiting for the API…' : 'Pick an endpoint and send the request to see the raw answer here.'}
        </div>
      )}
    </div>
  )
}
