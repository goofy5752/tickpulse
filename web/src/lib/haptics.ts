/** How long one tick vibrates, in ms: a light tap, like a detent on a dial. */
export const HAPTIC_TICK_MS = 8

let iosSwitch: HTMLLabelElement | null = null

function isAppleTouchDevice(): boolean {
  const ua = navigator.userAgent
  return /iP(hone|ad|od)/.test(ua) || (ua.includes('Macintosh') && navigator.maxTouchPoints > 1)
}

/**
 * Safari has no Vibration API. Since iOS 18 a native switch control (`<input type="checkbox" switch>`)
 * gives a light haptic when it toggles, so a hidden one is toggled through its label. Best effort:
 * iOS may only honour it inside a tap, not while a finger slides.
 */
function iosToggle(): HTMLLabelElement {
  if (iosSwitch?.isConnected) return iosSwitch
  const label = document.createElement('label')
  label.setAttribute('aria-hidden', 'true')
  label.dataset.haptics = ''
  label.style.cssText = 'position:fixed;left:-9999px;top:0;width:1px;height:1px;overflow:hidden;opacity:0;pointer-events:none'
  const input = document.createElement('input')
  input.type = 'checkbox'
  input.setAttribute('switch', '')
  input.tabIndex = -1
  label.append(input)
  document.body.append(label)
  iosSwitch = label
  return label
}

/** One light haptic tick, where the device and browser allow it; otherwise nothing. */
export function hapticTick(): void {
  if (typeof navigator === 'undefined') return
  if (typeof navigator.vibrate === 'function') {
    navigator.vibrate(HAPTIC_TICK_MS)
    return
  }
  if (typeof document !== 'undefined' && isAppleTouchDevice()) iosToggle().click()
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
