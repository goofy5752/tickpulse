import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { afterEach, describe, expect, it } from 'vitest'
import { SuggestInput } from './SuggestInput'

afterEach(() => {
  cleanup()
})

function setup() {
  function Harness() {
    const [value, setValue] = useState('')
    return (
      <>
        <label htmlFor="sym">Symbol</label>
        <SuggestInput id="sym" value={value} onChange={setValue} suggestions={['BTCUSDT', 'ETHUSDT', 'ADAUSDT']} />
      </>
    )
  }
  render(<Harness />)
  return { user: userEvent.setup(), input: screen.getByRole<HTMLInputElement>('combobox', { name: 'Symbol' }) }
}

describe('SuggestInput', () => {
  it('SuggestInput_Typing_FiltersTheList_AndAClickPicks', async () => {
    const { user, input } = setup()

    await user.type(input, 'eth')
    expect(screen.getAllByRole('option').map((option) => option.textContent)).toEqual(['ETHUSDT'])

    await user.click(screen.getByRole('option', { name: 'ETHUSDT' }))
    expect(input.value).toBe('ETHUSDT')
    expect(screen.queryByRole('listbox')).toBeNull()
  })

  it('SuggestInput_Keyboard_ArrowDownAndEnterPick_EscapeCloses', async () => {
    const { user, input } = setup()

    await user.click(input)
    expect(screen.getAllByRole('option')).toHaveLength(3)
    await user.keyboard('{ArrowDown}{ArrowDown}{Enter}')
    expect(input.value).toBe('ETHUSDT')

    await user.clear(input)
    await user.type(input, 'usdt')
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('listbox')).toBeNull()
    // Anything may be typed: the list only suggests.
    expect(input.value).toBe('usdt')
  })
})
