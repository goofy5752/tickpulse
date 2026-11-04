import { useCallback, useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { createPortal } from 'react-dom'
import { useAnchoredPopover } from '@/hooks/useAnchoredPopover'
import { control } from '@/lib/styles'
import { cn } from '@/lib/utils'

/** A minute in UTC as the SMA form keeps it: "2026-10-14T00:00", or "" for none. */
export type UtcMinute = string

interface DateTimePickerProps {
  /** The trigger's id, so a `<label htmlFor>` names it. */
  id: string
  value: UtcMinute
  onChange: (value: UtcMinute) => void
  /** Shown while no minute is picked. */
  placeholder?: string
  'aria-describedby'?: string
  className?: string
  /** The latest minute that may be picked; defaults to now. Tests pass a fixed clock. */
  now?: () => Date
}

const WEEKDAYS = ['Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa', 'Su'] as const
const MONTH = new Intl.DateTimeFormat('en-GB', { month: 'long', year: 'numeric', timeZone: 'UTC' })
const DAY_LABEL = new Intl.DateTimeFormat('en-GB', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' })
const SHOWN = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' })
const DAY_MS = 86_400_000

const pad = (value: number) => String(value).padStart(2, '0')
/** "2026-11-04" for a UTC midnight. */
const dayKey = (ms: number) => new Date(ms).toISOString().slice(0, 10)
const startOfDay = (ms: number) => ms - (((ms % DAY_MS) + DAY_MS) % DAY_MS)

function parse(value: UtcMinute): { day: number; hour: number; minute: number } | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(value)
  if (!match) return null
  const [, y, mo, d, h, mi] = match.map(Number) as [number, number, number, number, number, number]
  return { day: Date.UTC(y, mo - 1, d), hour: h, minute: mi }
}

const format = (day: number, hour: number, minute: number): UtcMinute => `${dayKey(day)}T${pad(hour)}:${pad(minute)}`

/** Six Monday-first weeks around the month that `monthStart` (UTC midnight of its 1st) opens. */
function monthGrid(monthStart: number): number[] {
  const offset = (new Date(monthStart).getUTCDay() + 6) % 7
  const first = monthStart - offset * DAY_MS
  return Array.from({ length: 42 }, (_, i) => first + i * DAY_MS)
}

function monthOf(ms: number): number {
  const date = new Date(ms)
  return Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), 1)
}

function addMonths(monthStart: number, delta: number): number {
  const date = new Date(monthStart)
  return Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + delta, 1)
}

/**
 * A UTC date and time picker in the dashboard's glass style, in place of the browser's
 * `datetime-local` popup: a month grid (Monday first, future days disabled) and hour and minute
 * fields. Picking a day keeps the time already set; a minute after `now` is moved back to it.
 * Keyboard: Arrow keys move by a day or a week, Page Up and Down by a month, Home and End to the
 * week's ends, Enter or Space picks, Escape closes.
 */
export function DateTimePicker({
  id,
  value,
  onChange,
  placeholder = 'Pick a date',
  'aria-describedby': describedBy,
  className,
  now = () => new Date(),
}: DateTimePickerProps) {
  const [open, setOpen] = useState(false)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const panelRef = useRef<HTMLDivElement>(null)
  const parsed = parse(value)

  const nowMs = now().getTime()
  const today = startOfDay(nowMs)
  const [month, setMonth] = useState(() => monthOf(parsed?.day ?? today))
  const [focusDay, setFocusDay] = useState(() => parsed?.day ?? today)
  /** Focus follows keyboard moves only, never a re-render. */
  const moved = useRef(false)

  const close = useCallback((reason: 'escape' | 'outside' | 'done') => {
    setOpen(false)
    if (reason !== 'outside') triggerRef.current?.focus()
  }, [])
  useAnchoredPopover(open, triggerRef, panelRef, { onClose: close })

  useEffect(() => {
    if (!open || !moved.current) return
    moved.current = false
    panelRef.current?.querySelector<HTMLButtonElement>(`[data-day="${dayKey(focusDay)}"]`)?.focus()
  }, [open, focusDay])

  const show = () => {
    const start = parsed?.day ?? today
    setMonth(monthOf(start))
    setFocusDay(start)
    moved.current = true
    setOpen(true)
  }

  /** Emits a minute, never later than now. */
  const emit = (day: number, hour: number, minute: number) => {
    const at = day + hour * 3_600_000 + minute * 60_000
    if (at > nowMs) {
      const clamped = new Date(nowMs)
      onChange(format(startOfDay(nowMs), clamped.getUTCHours(), clamped.getUTCMinutes()))
      return
    }
    onChange(format(day, hour, minute))
  }

  const pickDay = (day: number) => {
    if (day > today) return
    setFocusDay(day)
    if (monthOf(day) !== month) setMonth(monthOf(day))
    emit(day, parsed?.hour ?? 0, parsed?.minute ?? 0)
  }

  const moveFocus = (day: number) => {
    const next = Math.min(day, today)
    moved.current = true
    setFocusDay(next)
    if (monthOf(next) !== month) setMonth(monthOf(next))
  }

  const onGridKey = (event: KeyboardEvent) => {
    const weekday = (new Date(focusDay).getUTCDay() + 6) % 7
    const date = new Date(focusDay)
    const shiftMonth = (delta: number) =>
      Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + delta, Math.min(date.getUTCDate(), 28))
    const keys: Record<string, () => void> = {
      ArrowLeft: () => {
        moveFocus(focusDay - DAY_MS)
      },
      ArrowRight: () => {
        moveFocus(focusDay + DAY_MS)
      },
      ArrowUp: () => {
        moveFocus(focusDay - 7 * DAY_MS)
      },
      ArrowDown: () => {
        moveFocus(focusDay + 7 * DAY_MS)
      },
      Home: () => {
        moveFocus(focusDay - weekday * DAY_MS)
      },
      End: () => {
        moveFocus(focusDay + (6 - weekday) * DAY_MS)
      },
      PageUp: () => {
        moveFocus(shiftMonth(-1))
      },
      PageDown: () => {
        moveFocus(shiftMonth(1))
      },
    }
    const action = keys[event.key]
    if (!action) return
    event.preventDefault()
    action()
  }

  const nextMonth = addMonths(month, 1)
  const canGoNext = nextMonth <= today

  return (
    <>
      <button
        ref={triggerRef}
        id={id}
        type="button"
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-describedby={describedBy}
        onClick={() => {
          if (open) setOpen(false)
          else show()
        }}
        className={cn(control, 'flex cursor-pointer items-center justify-between gap-2 text-left', className)}
      >
        <span className={cn('truncate', !parsed && 'text-ink-faint')}>
          {parsed ? `${SHOWN.format(parsed.day)}, ${pad(parsed.hour)}:${pad(parsed.minute)}` : placeholder}
        </span>
        <CalendarIcon />
      </button>
      {open &&
        createPortal(
          <div ref={panelRef} role="dialog" aria-label="Start, UTC" className="popover w-[17.5rem] p-3">
            <div className="flex items-center justify-between gap-2">
              <p className="text-[0.8125rem] font-semibold text-ink" aria-live="polite">
                {MONTH.format(month)}
              </p>
              <div className="flex items-center gap-1">
                <NavButton
                  label="Previous month"
                  onClick={() => {
                    setMonth(addMonths(month, -1))
                  }}
                  direction="previous"
                />
                <NavButton
                  label="Next month"
                  disabled={!canGoNext}
                  onClick={() => {
                    setMonth(nextMonth)
                  }}
                  direction="next"
                />
              </div>
            </div>

            <div role="grid" aria-label={MONTH.format(month)} onKeyDown={onGridKey} className="mt-2.5">
              <div role="row" className="grid grid-cols-7">
                {WEEKDAYS.map((weekday) => (
                  <span key={weekday} role="columnheader" className="py-1 text-center text-[0.6875rem] font-medium text-ink-faint">
                    {weekday}
                  </span>
                ))}
              </div>
              {Array.from({ length: 6 }, (_, week) => (
                <div key={week} role="row" className="grid grid-cols-7">
                  {monthGrid(month)
                    .slice(week * 7, week * 7 + 7)
                    .map((day) => {
                      const inMonth = monthOf(day) === month
                      const future = day > today
                      const selected = parsed?.day === day
                      const key = dayKey(day)
                      return (
                        <span key={key} role="gridcell" aria-selected={selected}>
                          <button
                            type="button"
                            data-day={key}
                            tabIndex={day === focusDay ? 0 : -1}
                            disabled={future}
                            aria-label={DAY_LABEL.format(day)}
                            aria-current={day === today ? 'date' : undefined}
                            onClick={() => {
                              pickDay(day)
                            }}
                            className={cn(
                              'num mx-auto grid size-8 place-items-center rounded-full text-[0.8125rem] transition-colors duration-150',
                              inMonth ? 'text-ink' : 'text-ink-faint/70',
                              !selected && !future && 'hover:bg-glass-strong',
                              day === today && !selected && 'shadow-[inset_0_0_0_1px_var(--color-sma)]',
                              selected && 'bg-sma font-semibold text-bg',
                              future && 'cursor-not-allowed opacity-30',
                              'focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-sma',
                            )}
                          >
                            {new Date(day).getUTCDate()}
                          </button>
                        </span>
                      )
                    })}
                </div>
              ))}
            </div>

            <div className="mt-3 flex items-center gap-2 border-t border-tip-border pt-3">
              <span className="text-[0.75rem] text-ink-muted">Time</span>
              <TimeField
                label="Hour"
                value={parsed?.hour}
                max={23}
                disabled={!parsed}
                onChange={(hour) => {
                  if (parsed) emit(parsed.day, hour, parsed.minute)
                }}
              />
              <span className="text-ink-faint">:</span>
              <TimeField
                label="Minute"
                value={parsed?.minute}
                max={59}
                disabled={!parsed}
                onChange={(minute) => {
                  if (parsed) emit(parsed.day, parsed.hour, minute)
                }}
              />
              <span className="text-[0.75rem] text-ink-faint">UTC</span>
            </div>

            <div className="mt-3 flex items-center justify-between">
              <div className="flex gap-1">
                <FooterButton
                  onClick={() => {
                    onChange('')
                  }}
                >
                  Clear
                </FooterButton>
                <FooterButton
                  onClick={() => {
                    setMonth(monthOf(today))
                    pickDay(today)
                  }}
                >
                  Today
                </FooterButton>
              </div>
              <button
                type="button"
                onClick={() => {
                  close('done')
                }}
                className="h-7 rounded-full bg-ink px-3 text-[0.75rem] font-semibold text-bg transition-opacity duration-150 hover:opacity-90"
              >
                Done
              </button>
            </div>
          </div>,
          document.body,
        )}
    </>
  )
}

/** Two digits with Arrow Up and Down stepping; commits on every valid edit. */
function TimeField({
  label,
  value,
  max,
  disabled,
  onChange,
}: {
  label: string
  value: number | undefined
  max: number
  disabled: boolean
  onChange: (value: number) => void
}) {
  const [draft, setDraft] = useState<string | null>(null)
  const shown = draft ?? (value === undefined ? '' : pad(value))
  return (
    <input
      type="text"
      inputMode="numeric"
      aria-label={label}
      maxLength={2}
      placeholder="00"
      disabled={disabled}
      value={shown}
      onChange={(event) => {
        const text = event.target.value.replace(/\D/g, '')
        setDraft(text)
        const number = Number(text)
        if (text !== '' && number <= max) onChange(number)
      }}
      onBlur={() => {
        setDraft(null)
      }}
      onKeyDown={(event) => {
        if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return
        event.preventDefault()
        const current = value ?? 0
        setDraft(null)
        onChange((current + (event.key === 'ArrowUp' ? 1 : max)) % (max + 1))
      }}
      className="num h-8 w-11 rounded-lg border border-glass-border bg-glass-strong text-center text-[0.875rem] text-ink transition-colors duration-150 placeholder:text-ink-faint hover:border-ink-faint/40 focus-visible:border-sma/60 focus-visible:outline-none disabled:cursor-not-allowed disabled:opacity-40"
    />
  )
}

function NavButton({
  label,
  direction,
  disabled = false,
  onClick,
}: {
  label: string
  direction: 'previous' | 'next'
  disabled?: boolean
  onClick: () => void
}) {
  return (
    <button
      type="button"
      aria-label={label}
      disabled={disabled}
      onClick={onClick}
      className="grid size-7 place-items-center rounded-full text-ink-muted transition-colors duration-150 hover:bg-glass-strong hover:text-ink disabled:cursor-not-allowed disabled:opacity-30 disabled:hover:bg-transparent"
    >
      <svg viewBox="0 0 12 12" aria-hidden="true" className={cn('size-3', direction === 'next' && 'rotate-180')} fill="none">
        <path d="M7.5 3 4.5 6l3 3" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </button>
  )
}

function FooterButton({ onClick, children }: { onClick: () => void; children: string }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="h-7 rounded-full px-2.5 text-[0.75rem] font-medium text-sma transition-colors duration-150 hover:bg-sma-soft"
    >
      {children}
    </button>
  )
}

function CalendarIcon() {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true" className="size-4 shrink-0 text-ink-faint" fill="none">
      <rect x="2.25" y="3.25" width="11.5" height="10.5" rx="2" stroke="currentColor" strokeWidth="1.3" />
      <path d="M2.5 6.5h11M5.5 1.75v3M10.5 1.75v3" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
    </svg>
  )
}
