import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'
import { HELP } from '@/content/help'
import { placeTip } from '@/lib/placeTip'
import { InfoTip } from './InfoTip'

afterEach(() => {
  cleanup()
})

function setup() {
  const user = userEvent.setup()
  render(
    <div>
      <button type="button">Before</button>
      <InfoTip topic="smaN" />
      <button type="button">After</button>
    </div>,
  )
  return { user, trigger: screen.getByRole('button', { name: 'About Points (n)' }) }
}

describe('InfoTip', () => {
  it('InfoTip_IsClosedAndUndescribed_UntilOpened', () => {
    const { trigger } = setup()
    expect(screen.queryByRole('tooltip')).toBeNull()
    expect(trigger.getAttribute('aria-describedby')).toBeNull()
    expect(trigger.dataset.help).toBe('smaN')
  })

  it('InfoTip_OpensOnHover_WiresAriaDescribedby_AndClosesOnLeave', async () => {
    const { user, trigger } = setup()
    await user.hover(trigger)
    const tip = await screen.findByRole('tooltip')
    expect(trigger.getAttribute('aria-describedby')).toBe(tip.id)
    expect(tip.textContent).toContain(HELP.smaN.summary)
    expect(tip.textContent).toContain(HELP.smaN.example)

    await user.unhover(trigger)
    await waitFor(() => {
      expect(screen.queryByRole('tooltip')).toBeNull()
    })
    expect(trigger.getAttribute('aria-describedby')).toBeNull()
  })

  it('InfoTip_StaysOpen_WhilePointerMovesOntoTheTooltip', async () => {
    const { user, trigger } = setup()
    await user.hover(trigger)
    const tip = await screen.findByRole('tooltip')
    await user.unhover(trigger)
    await user.hover(tip)
    await new Promise((resolve) => setTimeout(resolve, 250))
    expect(screen.getByRole('tooltip')).toBe(tip)
  })

  it('InfoTip_OpensOnKeyboardFocus_ClosesOnEscape_KeepsFocus', async () => {
    const { user, trigger } = setup()
    await user.tab()
    await user.tab()
    expect(document.activeElement).toBe(trigger)
    const tip = screen.getByRole('tooltip')
    expect(trigger.getAttribute('aria-describedby')).toBe(tip.id)

    await user.keyboard('{Escape}')
    expect(screen.queryByRole('tooltip')).toBeNull()
    expect(document.activeElement).toBe(trigger)

    // Enter brings it back for someone who dismissed it.
    await user.keyboard('{Enter}')
    expect(screen.getByRole('tooltip')).toBeTruthy()
  })

  it('InfoTip_ClosesWhenFocusLeaves', async () => {
    const { user } = setup()
    await user.tab()
    await user.tab()
    expect(screen.getByRole('tooltip')).toBeTruthy()
    await user.tab()
    expect(screen.queryByRole('tooltip')).toBeNull()
  })

  it('InfoTip_TogglesOnTap_AndClosesOnOutsideTap', async () => {
    const { user, trigger } = setup()
    await user.pointer({ keys: '[TouchA]', target: trigger })
    expect(screen.getByRole('tooltip')).toBeTruthy()
    await user.pointer({ keys: '[TouchA]', target: trigger })
    expect(screen.queryByRole('tooltip')).toBeNull()

    await user.pointer({ keys: '[TouchA]', target: trigger })
    expect(screen.getByRole('tooltip')).toBeTruthy()
    await user.pointer({ keys: '[TouchA]', target: document.body })
    expect(screen.queryByRole('tooltip')).toBeNull()
  })

  it('InfoTip_ClosesOnOutsideClick', async () => {
    const { user, trigger } = setup()
    await user.click(trigger)
    expect(screen.getByRole('tooltip')).toBeTruthy()
    await user.click(screen.getByRole('button', { name: 'After' }))
    expect(screen.queryByRole('tooltip')).toBeNull()
  })

  it('InfoTip_CustomTrigger_IsNamedByItsContent_AndDescribedByTheTip', async () => {
    const user = userEvent.setup()
    render(
      <InfoTip topic="navStatus">
        <span>Binance live</span>
      </InfoTip>,
    )
    const trigger = screen.getByRole('button', { name: 'Binance live' })
    await user.tab()
    expect(document.activeElement).toBe(trigger)
    const tip = screen.getByRole('tooltip')
    expect(trigger.getAttribute('aria-describedby')).toBe(tip.id)
    expect(tip.textContent).toContain(HELP.navStatus.summary)
  })

  it('InfoTip_RendersTermsInAList', async () => {
    const user = userEvent.setup()
    render(<InfoTip topic="adminBackfill" />)
    await user.click(screen.getByRole('button', { name: 'About Backfill status' }))
    const tip = screen.getByRole('tooltip')
    for (const item of HELP.adminBackfill.items) {
      expect(tip.textContent).toContain(item.term)
      expect(tip.textContent).toContain(item.text)
    }
  })
})

describe('placeTip', () => {
  const size = { width: 320, height: 180 }

  it('PlaceTip_KeepsTheTooltipInsideA390pxViewport', () => {
    for (const x of [0, 20, 195, 360, 389]) {
      const trigger = { left: x, right: x + 16, top: 400, bottom: 416, width: 16, height: 16 }
      const placed = placeTip(trigger, size, 'top', { width: 390, height: 844 })
      expect(placed.left).toBeGreaterThanOrEqual(12)
      expect(placed.left + size.width).toBeLessThanOrEqual(390 - 12)
      expect(placed.arrowX).toBeGreaterThanOrEqual(12)
      expect(placed.arrowX).toBeLessThanOrEqual(size.width - 12)
    }
  })

  it('PlaceTip_FlipsBelow_WhenThereIsNoRoomAbove', () => {
    const trigger = { left: 100, right: 116, top: 30, bottom: 46, width: 16, height: 16 }
    const placed = placeTip(trigger, size, 'top', { width: 390, height: 844 })
    expect(placed.side).toBe('bottom')
    expect(placed.top).toBeGreaterThanOrEqual(46)
  })

  it('PlaceTip_CapsTheHeight_WhenNeitherSideFits', () => {
    const trigger = { left: 100, right: 116, top: 200, bottom: 216, width: 16, height: 16 }
    const placed = placeTip(trigger, { width: 320, height: 600 }, 'top', { width: 390, height: 420 })
    expect(placed.maxHeight).toBeLessThan(600)
    expect(placed.top).toBeGreaterThanOrEqual(12)
    expect(placed.top + placed.maxHeight).toBeLessThanOrEqual(420 - 12)
  })
})
