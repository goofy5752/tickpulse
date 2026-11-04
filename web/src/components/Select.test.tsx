import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Select } from './Select'

afterEach(() => {
  cleanup()
})

const OPTIONS = [
  { value: '1m', label: '1m' },
  { value: '5m', label: '5m' },
  { value: '1d', label: '1d' },
] as const

type Value = (typeof OPTIONS)[number]['value']

function setup(onChange = vi.fn()) {
  function Harness() {
    const [value, setValue] = useState<Value>('5m')
    return (
      <>
        <label htmlFor="p">Period</label>
        <Select
          id="p"
          value={value}
          options={OPTIONS}
          onChange={(next) => {
            setValue(next)
            onChange(next)
          }}
        />
        <button type="button">Elsewhere</button>
      </>
    )
  }
  render(<Harness />)
  return { user: userEvent.setup(), onChange, trigger: screen.getByRole('combobox', { name: 'Period' }) }
}

describe('Select', () => {
  it('Select_Click_OpensTheListOnTheCurrentValue_AndAClickPicks', async () => {
    const { user, onChange, trigger } = setup()
    expect(trigger.textContent).toBe('5m')

    await user.click(trigger)
    expect(trigger.getAttribute('aria-expanded')).toBe('true')
    expect(screen.getByRole('option', { name: '5m' }).getAttribute('aria-selected')).toBe('true')

    await user.click(screen.getByRole('option', { name: '1d' }))

    expect(onChange).toHaveBeenCalledWith('1d')
    expect(screen.queryByRole('listbox')).toBeNull()
    expect(trigger.textContent).toBe('1d')
    expect(document.activeElement).toBe(trigger)
  })

  it('Select_Keyboard_MovesAndPicks_LikeANativeSelect', async () => {
    const { user, onChange, trigger } = setup()
    trigger.focus()

    await user.keyboard('{ArrowDown}')
    const list = await screen.findByRole('listbox')
    await waitForFocus(list)
    await user.keyboard('{ArrowDown}{Enter}')

    expect(onChange).toHaveBeenCalledWith('1d')
    expect(document.activeElement).toBe(trigger)
  })

  it('Select_EscapeOrOutsidePress_ClosesWithoutChanging', async () => {
    const { user, onChange, trigger } = setup()

    await user.click(trigger)
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('listbox')).toBeNull()

    await user.click(trigger)
    await user.click(screen.getByRole('button', { name: 'Elsewhere' }))
    expect(screen.queryByRole('listbox')).toBeNull()
    expect(onChange).not.toHaveBeenCalled()
  })
})

async function waitForFocus(element: HTMLElement) {
  await new Promise((resolve) => requestAnimationFrame(resolve))
  expect(document.activeElement).toBe(element)
}
