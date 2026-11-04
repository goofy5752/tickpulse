/** Distance kept between a tooltip and the viewport edges. */
const VIEWPORT_MARGIN = 12
/** Space between the trigger and the tooltip. */
const GAP = 8
/** How far from the tooltip's edges its origin (where it grows from) may sit. */
const ORIGIN_INSET = 14

export type TipSide = 'top' | 'bottom'

interface Box {
  left: number
  top: number
  bottom: number
  width: number
}

export interface TipPlacement {
  left: number
  top: number
  side: TipSide
  /** The trigger's centre, relative to the tooltip's left edge. */
  arrowX: number
  /** The room on the chosen side; taller content scrolls inside. */
  maxHeight: number
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), Math.max(min, max))
}

/**
 * Where a tooltip of `size` goes next to `trigger` (viewport coordinates): centred on the trigger,
 * shifted to stay `VIEWPORT_MARGIN` inside the viewport, and flipped to the other side when the
 * preferred one is too short and the other has more room.
 */
export function placeTip(
  trigger: Box,
  size: { width: number; height: number },
  preferred: TipSide,
  viewport: { width: number; height: number },
): TipPlacement {
  const width = Math.min(size.width, viewport.width - VIEWPORT_MARGIN * 2)
  const centre = trigger.left + trigger.width / 2
  const left = clamp(centre - width / 2, VIEWPORT_MARGIN, viewport.width - VIEWPORT_MARGIN - width)

  const room = (side: TipSide) =>
    side === 'top' ? trigger.top - GAP - VIEWPORT_MARGIN : viewport.height - trigger.bottom - GAP - VIEWPORT_MARGIN
  const other: TipSide = preferred === 'top' ? 'bottom' : 'top'
  const side = size.height > room(preferred) && room(other) > room(preferred) ? other : preferred

  const maxHeight = Math.max(0, room(side))
  const height = Math.min(size.height, maxHeight)
  const top = side === 'top' ? trigger.top - GAP - height : trigger.bottom + GAP
  return { left, top, side, arrowX: clamp(centre - left, ORIGIN_INSET, width - ORIGIN_INSET), maxHeight }
}
