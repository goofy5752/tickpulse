import { afterEach, describe, expect, it, vi } from 'vitest'
import { createBucketHaptics, HAPTIC_TICK_MS, hapticTick } from './haptics'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
  for (const el of document.querySelectorAll('[data-haptics]')) el.remove()
})

describe('createBucketHaptics', () => {
  it('BucketHaptics_FingerAcrossThreeBuckets_TicksThreeTimes', () => {
    const tick = vi.fn()
    const move = createBucketHaptics(tick)
    const t = (hhmm: string) => Date.parse(`2026-11-05T${hhmm}:00Z`) / 1000

    // 30m candles: the finger slides from 01:30 through 02:00 to 02:30, with several moves inside each.
    for (const time of [t('01:30'), t('01:30'), t('02:00'), t('02:00'), t('02:00'), t('02:30')]) move(time, true)

    expect(tick).toHaveBeenCalledTimes(3)
  })

  it('BucketHaptics_Mouse_NeverTicks', () => {
    const tick = vi.fn()
    const move = createBucketHaptics(tick)

    move(100, false)
    move(200, false)

    expect(tick).not.toHaveBeenCalled()
  })

  it('BucketHaptics_LeavingTheChart_StartsOver', () => {
    const tick = vi.fn()
    const move = createBucketHaptics(tick)

    move(100, true)
    move(undefined, true)
    move(100, true)

    expect(tick).toHaveBeenCalledTimes(2)
  })
})

describe('hapticTick', () => {
  it('HapticTick_VibrationApi_VibratesBriefly', () => {
    const vibrate = vi.fn(() => true)
    vi.stubGlobal('navigator', { vibrate, userAgent: 'Mozilla/5.0 (Linux; Android 15)', maxTouchPoints: 5 })

    hapticTick()

    expect(vibrate).toHaveBeenCalledWith(HAPTIC_TICK_MS)
  })

  it('HapticTick_iPhoneWithoutVibration_TogglesAHiddenSwitch', () => {
    vi.stubGlobal('navigator', { userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)', maxTouchPoints: 5 })
    const click = vi.spyOn(HTMLElement.prototype, 'click')

    hapticTick()

    const label = document.querySelector<HTMLLabelElement>('label[data-haptics]')
    expect(label?.querySelector('input[switch]')).toBeTruthy()
    expect(label?.getAttribute('aria-hidden')).toBe('true')
    expect(click).toHaveBeenCalledTimes(1)
  })

  it('HapticTick_DesktopWithoutVibration_DoesNothing', () => {
    vi.stubGlobal('navigator', { userAgent: 'Mozilla/5.0 (Windows NT 10.0)', maxTouchPoints: 0 })

    hapticTick()

    expect(document.querySelector('[data-haptics]')).toBeNull()
  })
})
