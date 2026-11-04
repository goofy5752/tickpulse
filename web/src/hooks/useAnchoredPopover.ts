import { useEffect, useLayoutEffect, type RefObject } from 'react'

/** Distance kept from the viewport edges. */
const MARGIN = 12
/** Space between the trigger and the panel. */
const GAP = 6
/** Room kept above a trigger for the sticky nav when scrolling a panel into view. */
const NAV_CLEARANCE = 88

export interface AnchoredPopoverOptions {
  /** The panel is at least as wide as its trigger (a select's list). */
  matchWidth?: boolean
  /** Called with 'escape' for the Escape key and 'outside' for a press outside both elements. */
  onClose: (reason: 'escape' | 'outside') => void
}

/**
 * Keeps a portalled panel (`position: fixed`) next to its trigger while `open`: below it when it
 * fits, above it when that side has more room, inside the viewport, with the room left as its
 * max-height. It follows scrolling and resizing; scrolling inside the panel itself moves nothing.
 * Escape (capture phase, so only the panel closes) and a press outside both elements close it.
 */
export function useAnchoredPopover(
  open: boolean,
  triggerRef: RefObject<HTMLElement | null>,
  panelRef: RefObject<HTMLElement | null>,
  { matchWidth = false, onClose }: AnchoredPopoverOptions,
): void {
  useLayoutEffect(() => {
    if (!open) return
    const place = () => {
      const trigger = triggerRef.current
      const panel = panelRef.current
      if (!trigger || !panel) return
      const rect = trigger.getBoundingClientRect()
      const viewportWidth = document.documentElement.clientWidth || window.innerWidth
      const viewportHeight = window.innerHeight
      if (matchWidth) panel.style.minWidth = `${rect.width}px`
      panel.style.maxHeight = ''
      const width = panel.offsetWidth
      const height = panel.offsetHeight
      const below = viewportHeight - rect.bottom - GAP - MARGIN
      const above = rect.top - GAP - MARGIN
      const side = height <= below || below >= above ? 'bottom' : 'top'
      const room = Math.max(120, side === 'bottom' ? below : above)
      const shown = Math.min(height, room)
      panel.style.maxHeight = `${room}px`
      panel.style.top = `${side === 'bottom' ? rect.bottom + GAP : rect.top - GAP - shown}px`
      panel.style.left = `${Math.min(Math.max(rect.left, MARGIN), Math.max(MARGIN, viewportWidth - width - MARGIN))}px`
      panel.dataset.side = side
    }
    place()
    // Opening near the bottom of the window: scroll just enough for the panel to fit below its
    // trigger, while the trigger stays clear of the sticky nav, instead of opening upwards under it.
    const trigger = triggerRef.current
    const panel = panelRef.current
    if (trigger && panel?.dataset.side === 'top') {
      const rect = trigger.getBoundingClientRect()
      // Rounded up, so a fractional pixel does not leave a scrollbar.
      const needed = Math.ceil(panel.scrollHeight - (window.innerHeight - rect.bottom - GAP - MARGIN)) + 2
      if (needed > 0 && rect.top - needed >= NAV_CLEARANCE) {
        window.scrollBy({ top: needed, behavior: 'instant' })
        place()
      }
    }
    let frame = 0
    const schedule = (event: Event) => {
      if (event.target instanceof Node && panelRef.current?.contains(event.target)) return
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(place)
    }
    window.addEventListener('scroll', schedule, { capture: true, passive: true })
    window.addEventListener('resize', schedule)
    return () => {
      cancelAnimationFrame(frame)
      window.removeEventListener('scroll', schedule, { capture: true })
      window.removeEventListener('resize', schedule)
    }
  }, [open, triggerRef, panelRef, matchWidth])

  useEffect(() => {
    if (!open) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      event.stopPropagation()
      onClose('escape')
    }
    const onPress = (event: PointerEvent) => {
      const target = event.target
      if (target instanceof Node && (triggerRef.current?.contains(target) || panelRef.current?.contains(target))) return
      onClose('outside')
    }
    document.addEventListener('keydown', onKey, true)
    document.addEventListener('pointerdown', onPress, true)
    return () => {
      document.removeEventListener('keydown', onKey, true)
      document.removeEventListener('pointerdown', onPress, true)
    }
  }, [open, triggerRef, panelRef, onClose])
}

/** Scrolls the option at `index` into view inside its list. */
export function revealOption(list: HTMLElement | null, index: number): void {
  const option = list?.querySelector(`[data-index="${String(index)}"]`)
  // jsdom has no scrollIntoView.
  if (option && typeof option.scrollIntoView === 'function') option.scrollIntoView({ block: 'nearest' })
}
