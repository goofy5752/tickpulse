import {
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from 'react'
import { createPortal } from 'react-dom'
import { HELP, type HelpEntry, type HelpKey } from '@/content/help'
import { placeTip, type TipSide } from '@/lib/placeTip'
import { cn } from '@/lib/utils'

/** Hover intent: a pointer passing over the icon does not flash a tooltip. */
const OPEN_DELAY_MS = 90
/** Time to move the pointer from the icon onto the tooltip (WCAG 1.4.13: hoverable). */
const CLOSE_DELAY_MS = 140

interface InfoTipProps {
  topic: HelpKey
  /** The side to open on when both have room. */
  placement?: TipSide
  /**
   * A custom trigger in place of the ⓘ icon (e.g. a status pill). It is named by its own content,
   * and `className` then styles the whole trigger.
   */
  children?: ReactNode
  className?: string
}

/** The ⓘ glyph, for triggers that show it inside their own content. */
export function InfoIcon({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true" className={cn('size-3.5 shrink-0', className)} fill="none">
      <circle cx="8" cy="8" r="6.4" stroke="currentColor" strokeWidth="1.25" />
      <circle cx="8" cy="5.05" r="0.95" fill="currentColor" />
      <path d="M8 7.3v4.1" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
    </svg>
  )
}

/**
 * A small ⓘ that explains the thing next to it. It opens on hover (after a short intent delay) and
 * on keyboard focus, toggles on tap, and closes on Escape, on an outside click or tap, and when
 * focus or the pointer leaves. While open, the trigger is described by the tooltip
 * (`aria-describedby` → `role="tooltip"`). The tooltip is portalled to <body>, so no panel's
 * overflow or backdrop-filter clips it, and it is positioned to stay inside the viewport.
 */
export function InfoTip({ topic, placement = 'top', children, className }: InfoTipProps) {
  const entry: HelpEntry = HELP[topic]
  const id = useId()
  const [open, setOpen] = useState(false)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const tipRef = useRef<HTMLDivElement>(null)
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined)
  /** The pointer type of the press in progress, so focus and click know a tap from a click. */
  const press = useRef<string | null>(null)

  const setNow = useCallback((next: boolean | ((open: boolean) => boolean)) => {
    clearTimeout(timer.current)
    setOpen(next)
  }, [])

  const setLater = useCallback((next: boolean, delay: number) => {
    clearTimeout(timer.current)
    timer.current = setTimeout(() => {
      setOpen(next)
    }, delay)
  }, [])

  useEffect(
    () => () => {
      clearTimeout(timer.current)
    },
    [],
  )

  // Position before paint, then follow scrolling (of any container) and resizing.
  useLayoutEffect(() => {
    if (!open) return
    const update = () => {
      const trigger = triggerRef.current
      const tip = tipRef.current
      if (!trigger || !tip) return
      tip.style.maxHeight = ''
      const placed = placeTip(
        trigger.getBoundingClientRect(),
        { width: tip.offsetWidth, height: tip.offsetHeight },
        placement,
        { width: document.documentElement.clientWidth || window.innerWidth, height: window.innerHeight },
      )
      tip.style.left = `${placed.left}px`
      tip.style.top = `${placed.top}px`
      tip.style.maxHeight = `${placed.maxHeight}px`
      tip.style.setProperty('--tip-origin-x', `${placed.arrowX}px`)
      tip.dataset.side = placed.side
    }
    update()
    let frame = 0
    const schedule = () => {
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(update)
    }
    window.addEventListener('scroll', schedule, { capture: true, passive: true })
    window.addEventListener('resize', schedule)
    return () => {
      cancelAnimationFrame(frame)
      window.removeEventListener('scroll', schedule, { capture: true })
      window.removeEventListener('resize', schedule)
    }
  }, [open, placement])

  // Escape and outside presses dismiss it.
  useEffect(() => {
    if (!open) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setNow(false)
    }
    const onPress = (event: PointerEvent) => {
      const target = event.target
      if (target instanceof Node && (triggerRef.current?.contains(target) || tipRef.current?.contains(target))) return
      setNow(false)
    }
    document.addEventListener('keydown', onKey)
    document.addEventListener('pointerdown', onPress, true)
    return () => {
      document.removeEventListener('keydown', onKey)
      document.removeEventListener('pointerdown', onPress, true)
    }
  }, [open, setNow])

  const hoverIn = (event: ReactPointerEvent) => {
    if (event.pointerType === 'mouse') setLater(true, OPEN_DELAY_MS)
  }
  const hoverOut = (event: ReactPointerEvent) => {
    if (event.pointerType === 'mouse') setLater(false, CLOSE_DELAY_MS)
  }
  const stayOpen = (event: ReactPointerEvent) => {
    if (event.pointerType === 'mouse') clearTimeout(timer.current)
  }

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        aria-label={children === undefined ? `About ${entry.title}` : undefined}
        aria-describedby={open ? id : undefined}
        data-help={topic}
        data-open={open ? '' : undefined}
        onPointerEnter={hoverIn}
        onPointerLeave={hoverOut}
        onPointerDown={(event) => {
          press.current = event.pointerType
        }}
        onFocus={() => {
          // A tap focuses the button too; the click that follows decides, so it can toggle.
          if (press.current === 'touch' || press.current === 'pen') return
          setNow(true)
        }}
        onBlur={() => {
          press.current = null
          setNow(false)
        }}
        onClick={() => {
          const kind = press.current
          press.current = null
          // A mouse click keeps it open (hover already opened it); a tap or Enter/Space toggles.
          if (kind === 'mouse') setNow(true)
          else setNow((value) => !value)
        }}
        className={
          children === undefined
            ? cn(
                'relative inline-grid size-4 shrink-0 cursor-help place-items-center rounded-full align-middle text-ink-faint transition-colors duration-150',
                'after:absolute after:-inset-1 after:rounded-full hover:text-ink focus-visible:rounded-full data-open:text-sma',
                className,
              )
            : cn('cursor-help', className)
        }
      >
        {children ?? <InfoIcon />}
      </button>
      {open &&
        createPortal(
          <div
            ref={tipRef}
            id={id}
            role="tooltip"
            data-side={placement}
            className="info-tip"
            onPointerEnter={stayOpen}
            onPointerLeave={hoverOut}
          >
            <TipBody entry={entry} />
          </div>,
          document.body,
        )}
    </>
  )
}

function TipBody({ entry }: { entry: HelpEntry }) {
  return (
    <div className="info-tip-body">
      <p className="info-tip-title">{entry.title}</p>
      <p className="info-tip-summary">{entry.summary}</p>
      {entry.items && (
        <dl className="info-tip-items">
          {entry.items.map((item) => (
            <div key={item.term} className="contents">
              <dt className={cn(item.mono && 'font-mono')}>{item.term}</dt>
              <dd>{item.text}</dd>
            </div>
          ))}
        </dl>
      )}
      {entry.details?.map((paragraph) => (
        <p key={paragraph} className="info-tip-detail">
          {paragraph}
        </p>
      ))}
      {entry.example && (
        <div className="info-tip-example">
          <p>Example</p>
          <pre>
            <code>{entry.example}</code>
          </pre>
        </div>
      )}
    </div>
  )
}
