/** How long one tick vibrates, in ms: a light tap, like a detent on a dial. */
export const HAPTIC_TICK_MS = 8

/**
 * One light haptic tick through the Vibration API (Android browsers). Safari on iOS has no Vibration API
 * and gives web pages no other haptics while a finger slides, so there it does nothing, as on desktops.
 */
export function hapticTick(): void {
  if (typeof navigator !== 'undefined' && typeof navigator.vibrate === 'function') navigator.vibrate(HAPTIC_TICK_MS)
}

/**
 * Ticks once each time a finger moves the chart's crosshair onto another bucket, the way price apps
 * click through candles. Mouse moves never tick, the same bucket never ticks twice in a row, and
 * leaving the chart starts over, so the next touch ticks on its first bucket.
 */
export function createBucketHaptics(tick: () => void = hapticTick) {
  let last: number | undefined
  return (time: number | undefined, byTouch: boolean): void => {
    if (time === undefined) {
      last = undefined
      return
    }
    if (time === last) return
    last = time
    if (byTouch) tick()
  }
}
