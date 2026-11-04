import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { DateTimePicker } from './DateTimePicker'

afterEach(() => {
  cleanup()
})

/** A fixed clock: Wednesday 4 November 2026, 16:20 UTC. */
const now = () => new Date(Date.UTC(2026, 10, 4, 16, 20))

function setup(initial = '') {
  const onChange = vi.fn()
  function Harness() {
    const [value, setValue] = useState(initial)
    return (
      <>
        <label htmlFor="s">Start</label>
        <DateTimePicker
          id="s"
          value={value}
          now={now}
          placeholder="Up to now"
          onChange={(next) => {
            setValue(next)
            onChange(next)
          }}
        />
      </>
    )
  }
  render(<Harness />)
  return { user: userEvent.setup(), onChange, trigger: screen.getByRole('button', { name: /Start/ }) }
}

describe('DateTimePicker', () => {
  it('DateTimePicker_PickADay_SetsItsMidnight_AndShowsIt', async () => {
    const { user, onChange, trigger } = setup()
    expect(trigger.textContent).toContain('Up to now')

    await user.click(trigger)
    expect(screen.getByRole('dialog', { name: 'Start, UTC' })).toBeTruthy()
    expect(screen.getByText('November 2026')).toBeTruthy()

    await user.click(screen.getByRole('button', { name: 'Monday, 2 November 2026' }))

    expect(onChange).toHaveBeenLastCalledWith('2026-11-02T00:00')
    expect(trigger.textContent).toContain('2 Nov 2026, 00:00')
  })

  it('DateTimePicker_FutureDays_CannotBePicked_AndNextMonthIsClosed', async () => {
    const { user, trigger } = setup()
    await user.click(trigger)

    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Thursday, 5 November 2026' }).disabled).toBe(true)
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Next month' }).disabled).toBe(true)
    expect(screen.getByRole('button', { name: 'Wednesday, 4 November 2026' }).getAttribute('aria-current')).toBe('date')
  })

  it('DateTimePicker_Time_KeepsTheDay_AndNeverPassesNow', async () => {
    const { user, onChange, trigger } = setup('2026-11-04T08:00')
    await user.click(trigger)

    const hour = screen.getByRole('textbox', { name: 'Hour' })
    await user.clear(hour)
    await user.type(hour, '10')
    expect(onChange).toHaveBeenLastCalledWith('2026-11-04T10:00')

    // 23:00 today is after 16:20, so it is moved back to now.
    await user.clear(hour)
    await user.type(hour, '23')
    expect(onChange).toHaveBeenLastCalledWith('2026-11-04T16:20')
  })

  it('DateTimePicker_Keyboard_MovesByDayWeekAndMonth', async () => {
    const { user, onChange, trigger } = setup('2026-10-14T00:00')
    await user.click(trigger)
    await new Promise((resolve) => requestAnimationFrame(resolve))
    expect(document.activeElement?.getAttribute('aria-label')).toBe('Wednesday, 14 October 2026')

    await user.keyboard('{ArrowRight}{ArrowDown}')
    expect(document.activeElement?.getAttribute('aria-label')).toBe('Thursday, 22 October 2026')

    await user.keyboard('{PageUp}')
    expect(screen.getByText('September 2026')).toBeTruthy()

    await user.keyboard('{Enter}')
    expect(onChange).toHaveBeenLastCalledWith('2026-09-22T00:00')
  })

  it('DateTimePicker_Clear_ReturnsToNow_AndEscapeCloses', async () => {
    const { user, onChange, trigger } = setup('2026-10-14T06:30')
    await user.click(trigger)

    await user.click(screen.getByRole('button', { name: 'Clear' }))
    expect(onChange).toHaveBeenLastCalledWith('')

    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(document.activeElement).toBe(trigger)
  })
})
