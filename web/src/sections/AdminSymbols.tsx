import { useMutation, useQueryClient } from '@tanstack/react-query'
import { memo, useEffect, useId, useMemo, useRef, useState, type SyntheticEvent } from 'react'
import { adminRequest, ApiError, apiPaths, MissingApiKeyError } from '@/api/client'
import { BACKFILL_POLL_MS, queryKeys, useSymbols } from '@/api/queries'
import type { BackfillStatus, SymbolResponse } from '@/api/types'
import { FieldLabel } from '@/components/FieldLabel'
import { InfoTip } from '@/components/InfoTip'
import { Skeleton } from '@/components/Skeleton'
import { TitleWithTip } from '@/components/TitleWithTip'
import { useSessionStorage } from '@/hooks/useSessionStorage'
import { splitSymbol } from '@/lib/format'
import { SECTION_IDS } from '@/lib/links'
import { control, primaryButton, quietButton, sectionLead, sectionTitle } from '@/lib/styles'
import { cn } from '@/lib/utils'

/** Looks like `disabled` but keeps the button focusable. */
const pendingButton = 'aria-disabled:cursor-not-allowed aria-disabled:opacity-45'

/** sessionStorage entry for the admin key: this tab only, gone when the tab closes. */
export const ADMIN_KEY_STORAGE_KEY = 'tickpulse.adminApiKey'

interface Notice {
  tone: 'success' | 'error'
  title: string
  detail?: string | undefined
}

/** Plain-language headline per problem, with the API's own `detail` kept underneath. */
function describeError(error: unknown, symbol: string): Notice {
  if (error instanceof MissingApiKeyError) {
    return { tone: 'error', title: 'Enter the admin API key first.', detail: 'Adding and removing symbols needs the key the server runs with.' }
  }
  if (!(error instanceof ApiError)) {
    return { tone: 'error', title: 'The API did not answer.', detail: 'Check that it is running, then try again.' }
  }
  const detail = error.problem?.detail ?? undefined
  const notice = (title: string, fallback?: string): Notice => ({ tone: 'error', title, detail: detail ?? fallback })
  switch (error.problemSlug) {
    case 'invalid-symbol':
      return notice(`${symbol} cannot be monitored.`, 'Use a Binance spot pair such as SOLUSDT.')
    case 'symbol-limit':
      return notice('The symbol limit is reached.', 'Remove a symbol before adding another.')
    case 'admin-disabled':
      return notice('Symbol management is turned off on this server.', 'It works once the server has an admin API key configured.')
    case 'unknown-symbol':
      return notice(`${symbol} is not monitored.`)
    case 'rate-limited':
      return notice('Too many requests.', 'Wait a moment, then try again.')
    case 'validation':
      return notice('The request was not valid.')
  }
  switch (error.status) {
    case 401:
      return notice('The API key was not accepted.', 'Check the key and try again.')
    case 403:
      return notice('Symbol management is turned off on this server.')
    case 404:
      return notice(`${symbol} is not monitored.`)
    default:
      return notice(`The request failed with status ${error.status}.`, error.message)
  }
}

const BACKFILL: Record<BackfillStatus, { label: string; className: string; dot?: string }> = {
  NotRequested: {
    label: 'Live only',
    className: 'border-glass-border text-ink-faint',
  },
  Pending: {
    label: 'Backfill queued',
    className: 'border-glass-border bg-glass-strong text-ink-muted',
    dot: 'bg-ink-faint status-dot-wait',
  },
  Running: {
    label: 'Backfilling',
    className: 'border-glass-border bg-glass-strong text-ink',
    dot: 'bg-up status-dot-wait',
  },
  Completed: {
    label: 'Backfilled',
    className: 'border-up/30 bg-up-soft text-up',
  },
  Failed: {
    label: 'Backfill failed',
    className: 'border-down/35 bg-down-soft text-down',
  },
}

function BackfillBadge({ status }: { status: BackfillStatus }) {
  const style = BACKFILL[status]
  return (
    <span
      className={cn('inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5 text-[0.75rem] whitespace-nowrap', style.className)}
    >
      {style.dot && <span aria-hidden="true" className={cn('relative size-1.5 rounded-full', style.dot)} />}
      {style.label}
    </span>
  )
}

/**
 * Add and remove monitored symbols. The list itself is public (shared with the ticker tiles);
 * changes go to the admin API with the key from this tab's sessionStorage. Removing asks for a
 * confirmation in place, and the list polls while a backfill is queued or running.
 */
export const AdminSymbols = memo(function AdminSymbols() {
  const ids = useId()
  const queryClient = useQueryClient()
  const symbols = useSymbols({ pollBackfill: true })
  const active = useMemo(() => (symbols.data ?? []).filter((s) => s.isActive), [symbols.data])
  const backfilling = active.some((s) => s.backfillStatus === 'Pending' || s.backfillStatus === 'Running')

  const [apiKey, setApiKey] = useSessionStorage(ADMIN_KEY_STORAGE_KEY)
  const [showKey, setShowKey] = useState(false)
  const [draft, setDraft] = useState('')
  const [notice, setNotice] = useState<Notice | null>(null)
  const [confirming, setConfirming] = useState<string | null>(null)

  const keyInput = useRef<HTMLInputElement>(null)
  const symbolInput = useRef<HTMLInputElement>(null)
  const keepButton = useRef<HTMLButtonElement>(null)
  const removeButtons = useRef(new Map<string, HTMLButtonElement>())
  /** Where focus goes once the confirm step closes; its buttons vanish, so focus must land somewhere real. */
  const restoreFocus = useRef<{ to: 'row'; symbol: string } | { to: 'symbol-input' } | null>(null)

  function fail(error: unknown, symbol: string) {
    setNotice(describeError(error, symbol))
    if (error instanceof MissingApiKeyError) keyInput.current?.focus()
    if (error instanceof ApiError && error.status === 401) keyInput.current?.focus()
  }

  function refresh() {
    void queryClient.invalidateQueries({ queryKey: queryKeys.symbols() })
  }

  const add = useMutation({
    mutationFn: (symbol: string) => adminRequest<SymbolResponse>('POST', apiPaths.adminSymbols(), apiKey, { symbol }),
    onSuccess: (response, symbol) => {
      if (response.status === 201) {
        setDraft('')
        setNotice({ tone: 'success', title: `Added ${symbol}.`, detail: 'Live trades start within seconds; the badge shows the backfill.' })
      } else {
        setNotice({ tone: 'success', title: `${symbol} is already monitored.` })
      }
      refresh()
    },
    onError: fail,
  })

  const remove = useMutation({
    mutationFn: (symbol: string) => adminRequest('DELETE', apiPaths.adminSymbol(symbol), apiKey),
    onSuccess: (_response, symbol) => {
      // The row is about to leave the list; continue from the add field.
      restoreFocus.current = { to: 'symbol-input' }
      setConfirming(null)
      setNotice({ tone: 'success', title: `Stopped monitoring ${symbol}.`, detail: 'Its history stays queryable.' })
      refresh()
    },
    onError: (error, symbol) => {
      const keyProblem = error instanceof MissingApiKeyError || (error instanceof ApiError && error.status === 401)
      const gone = error instanceof ApiError && error.status === 404
      // A key problem focuses the key field (in fail); a vanished row hands over to the add field;
      // anything else returns to the row so the person can try again.
      restoreFocus.current = keyProblem ? null : gone ? { to: 'symbol-input' } : { to: 'row', symbol }
      setConfirming(null)
      fail(error, symbol)
      if (gone) refresh()
    },
  })

  // The confirm step takes focus on its safe choice; closing it puts focus where restoreFocus says.
  useEffect(() => {
    if (confirming) {
      keepButton.current?.focus()
      return
    }
    const target = restoreFocus.current
    restoreFocus.current = null
    if (target?.to === 'row') removeButtons.current.get(target.symbol)?.focus()
    if (target?.to === 'symbol-input') symbolInput.current?.focus()
  }, [confirming])

  function submitAdd(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault()
    const symbol = draft.trim().toUpperCase()
    if (symbol.length === 0) {
      setNotice({ tone: 'error', title: 'Type a symbol to add, such as SOLUSDT.' })
      symbolInput.current?.focus()
      return
    }
    setNotice(null)
    add.mutate(symbol)
  }

  function askRemove(symbol: string) {
    if (apiKey.trim().length === 0) {
      fail(new MissingApiKeyError(), symbol)
      return
    }
    setNotice(null)
    setConfirming(symbol)
  }

  function keep() {
    // A request already on its way cannot be called back; the answer closes the step.
    if (remove.isPending || confirming === null) return
    restoreFocus.current = { to: 'row', symbol: confirming }
    setConfirming(null)
  }

  return (
    <section id={SECTION_IDS.admin} aria-labelledby={`${ids}-title`} className="glass flex flex-col p-5 sm:p-6">
      <TitleWithTip help="adminSection">
        <h2 id={`${ids}-title`} className={sectionTitle}>
          Monitored symbols
        </h2>
      </TitleWithTip>
      <p className={sectionLead}>Choose which Binance pairs TickPulse collects. Changes need the admin API key.</p>

      <div className="mt-5 grid grid-cols-1 gap-4 sm:grid-cols-2">
        <div className="min-w-0">
          <FieldLabel htmlFor={`${ids}-key`} help="adminKey">
            Admin API key
          </FieldLabel>
          <div className="flex gap-2">
            <input
              ref={keyInput}
              id={`${ids}-key`}
              type={showKey ? 'text' : 'password'}
              value={apiKey}
              onChange={(event) => {
                setApiKey(event.target.value)
              }}
              autoComplete="off"
              spellCheck={false}
              aria-describedby={`${ids}-key-hint`}
              className={cn(control, 'min-w-0 flex-1 font-mono text-[0.8125rem]')}
            />
            <button
              type="button"
              aria-label={showKey ? 'Hide key' : 'Show key'}
              aria-pressed={showKey}
              onClick={() => {
                setShowKey((value) => !value)
              }}
              className={cn(quietButton, 'w-9 px-0')}
            >
              <svg viewBox="0 0 20 20" aria-hidden="true" className="size-4" fill="none" stroke="currentColor" strokeWidth="1.5">
                <path strokeLinejoin="round" d="M1.8 10S4.8 4.5 10 4.5 18.2 10 18.2 10 15.2 15.5 10 15.5 1.8 10 1.8 10Z" />
                <circle cx="10" cy="10" r="2.6" />
                {showKey && <path strokeLinecap="round" d="M3 3l14 14" />}
              </svg>
            </button>
          </div>
          <div className="mt-1.5 flex flex-wrap items-baseline gap-x-2 text-[0.75rem] text-ink-faint">
            <p id={`${ids}-key-hint`}>Kept in this tab only and sent as the X-Api-Key header.</p>
            {apiKey.length > 0 && (
              <button
                type="button"
                onClick={() => {
                  setApiKey('')
                  keyInput.current?.focus()
                }}
                className="rounded text-ink-muted underline decoration-ink-faint/50 underline-offset-2 hover:text-ink"
              >
                Forget key
              </button>
            )}
          </div>
        </div>

        <form onSubmit={submitAdd} className="min-w-0" aria-label="Add a symbol">
          <FieldLabel htmlFor={`${ids}-symbol`} help="adminSymbol">
            Symbol to add
          </FieldLabel>
          <div className="flex items-center gap-2">
            <input
              ref={symbolInput}
              id={`${ids}-symbol`}
              value={draft}
              onChange={(event) => {
                setDraft(event.target.value)
              }}
              placeholder="SOLUSDT"
              autoComplete="off"
              spellCheck={false}
              autoCapitalize="characters"
              aria-describedby={`${ids}-symbol-hint`}
              className={cn(control, 'min-w-0 flex-1 font-mono text-[0.8125rem] uppercase placeholder:text-ink-faint placeholder:normal-case')}
            />
            <button type="submit" disabled={add.isPending} className={primaryButton}>
              {add.isPending ? 'Adding…' : 'Add symbol'}
            </button>
            <InfoTip topic="adminAdd" />
          </div>
          <p id={`${ids}-symbol-hint`} className="mt-1.5 text-[0.75rem] text-ink-faint">
            Any Binance spot pair that is trading.
          </p>
        </form>
      </div>

      <div className="mt-3 min-h-[1.25rem]">
        {notice?.tone === 'error' && (
          <div role="alert" className="rounded-(--radius-control) border border-down/35 bg-down-soft px-3 py-2 text-[0.8125rem]">
            <p className="font-medium text-ink">{notice.title}</p>
            {notice.detail && notice.detail !== notice.title && <p className="text-ink-muted">{notice.detail}</p>}
          </div>
        )}
        <div role="status">
          {notice?.tone === 'success' && (
            <div className="rounded-(--radius-control) border border-up/30 bg-up-soft px-3 py-2 text-[0.8125rem]">
              <p className="font-medium text-ink">{notice.title}</p>
              {notice.detail && <p className="text-ink-muted">{notice.detail}</p>}
            </div>
          )}
        </div>
      </div>

      <div className="mt-4 flex items-center justify-between gap-3 border-b border-glass-border pb-2">
        <div className="flex items-center gap-1.5">
          <h3 className="text-[0.8125rem] font-medium text-ink-muted">Active</h3>
          {symbols.data && <span className="num text-[0.75rem] text-ink-faint">{active.length}</span>}
          
        </div>
        <div className="flex items-center gap-3.5 text-[0.75rem] text-ink-faint">
          <span className="flex items-center gap-1">
            Backfill <InfoTip topic="adminBackfill" />
          </span>
          <span className="flex items-center gap-1">
            Remove <InfoTip topic="adminRemove" />
          </span>
        </div>
      </div>

      {symbols.isPending ? (
        <ul aria-hidden="true" className="divide-y divide-glass-border">
          {[0, 1, 2].map((i) => (
            <li key={i} className="flex items-center gap-3 py-3">
              <Skeleton className="h-4 w-24" />
              <Skeleton className="ml-auto h-5 w-20 rounded-full" />
            </li>
          ))}
        </ul>
      ) : symbols.isError && !symbols.data ? (
        <p className="py-4 text-ink-muted">Could not load the monitored symbols. The next refresh will try again.</p>
      ) : active.length === 0 ? (
        <p className="py-4 text-ink-muted">No symbols are monitored. Add one above to start collecting trades.</p>
      ) : (
        <ul className="divide-y divide-glass-border">
          {active.map((row) => {
            const { base, quote } = splitSymbol(row.symbol)
            const isConfirming = confirming === row.symbol
            return (
              <li key={row.symbol} className="py-2.5">
                {isConfirming ? (
                  <div
                    className="flex flex-wrap items-center gap-x-3 gap-y-2"
                    onKeyDown={(event) => {
                      if (event.key === 'Escape' && !remove.isPending) keep()
                    }}
                  >
                    <p className="min-w-0 flex-1 text-[0.8125rem] text-ink">
                      Stop monitoring {row.symbol}? <span className="text-ink-muted">Its history stays queryable.</span>
                    </p>
                    {/* aria-disabled, not disabled: a disabled button drops focus to <body> mid-request. */}
                    <div className="flex gap-2">
                      <button
                        type="button"
                        aria-disabled={remove.isPending}
                        onClick={() => {
                          if (!remove.isPending) remove.mutate(row.symbol)
                        }}
                        className={cn(quietButton, pendingButton, 'h-8 border-down/40 text-down hover:bg-down-soft hover:text-down')}
                      >
                        {remove.isPending ? 'Removing…' : 'Yes, remove'}
                      </button>
                      <button
                        ref={keepButton}
                        type="button"
                        onClick={keep}
                        aria-disabled={remove.isPending}
                        className={cn(quietButton, pendingButton, 'h-8')}
                      >
                        Keep
                      </button>
                    </div>
                  </div>
                ) : (
                  <div className="flex items-center gap-3">
                    <span className="min-w-0 flex-1 truncate text-[0.875rem] font-semibold tracking-tight">
                      {base}
                      {quote && <span className="font-normal text-ink-faint">/{quote}</span>}
                    </span>
                    <BackfillBadge status={row.backfillStatus} />
                    <button
                      ref={(element) => {
                        if (element) removeButtons.current.set(row.symbol, element)
                        else removeButtons.current.delete(row.symbol)
                      }}
                      type="button"
                      aria-label={`Remove ${row.symbol}`}
                      onClick={() => {
                        askRemove(row.symbol)
                      }}
                      className={cn(quietButton, 'h-8 border-transparent px-2.5 hover:border-glass-border')}
                    >
                      Remove
                    </button>
                  </div>
                )}
              </li>
            )
          })}
        </ul>
      )}

      {backfilling && (
        <p className="mt-3 flex items-center gap-2 text-[0.75rem] text-ink-faint">
          <span aria-hidden="true" className="status-dot-wait relative size-1.5 rounded-full bg-ink-faint" />
          {`Checking backfill progress every ${BACKFILL_POLL_MS / 1000} s.`}
        </p>
      )}
    </section>
  )
})
