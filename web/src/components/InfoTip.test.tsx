import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
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

  it('InfoTip_ScrollingItsOwnBody_DoesNotReplaceIt', async () => {
    const { user, trigger } = setup()
    await user.hover(trigger)
    const tip = await screen.findByRole('tooltip')
    const body = tip.querySelector<HTMLElement>('.info-tip-body')
    if (!body) throw new Error('no body')
    const nextFrame = () => new Promise((resolve) => requestAnimationFrame(resolve))

    // A marker the placement would overwrite: re-placing resets the height and snaps the scroll back.
    tip.style.maxHeight = '77px'
    body.dispatchEvent(new Event('scroll'))
    await nextFrame()
    expect(tip.style.maxHeight).toBe('77px')

    // Scrolling the page still re-places it.
    window.dispatchEvent(new Event('scroll'))
    await nextFrame()
    expect(tip.style.maxHeight).not.toBe('77px')
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

  it('InfoTip_Escape_ClosesOnlyTheTip_WhileItIsOpen', async () => {
    const escapes = vi.fn()
    const outer = (event: KeyboardEvent) => {
      if (event.key === 'Escape') escapes()
    }
    window.addEventListener('keydown', outer)
    try {
      const { user } = setup()
      await user.tab()
      await user.tab()
      expect(screen.getByRole('tooltip')).toBeTruthy()
      await user.keyboard('{Escape}')
      expect(screen.queryByRole('tooltip')).toBeNull()
      expect(escapes).not.toHaveBeenCalled()
      // Closed, a second Escape reaches the page (e.g. closes the phone menu).
      await user.keyboard('{Escape}')
      expect(escapes).toHaveBeenCalledTimes(1)
    } finally {
      window.removeEventListener('keydown', outer)
    }
  })

  it('InfoTip_OverflowingBody_IsReachableByKeyboard', async () => {
    // jsdom has no layout: make every tooltip body report more content than room.
    vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get').mockImplementation(function (this: HTMLElement) {
      return this.classList.contains('info-tip-body') ? 600 : 0
    })
    vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockImplementation(function (this: HTMLElement) {
      return this.classList.contains('info-tip-body') ? 200 : 0
    })
    const { user, trigger } = setup()
    await user.tab()
    await user.tab()
    const body = screen.getByRole('tooltip').querySelector<HTMLElement>('.info-tip-body')
    expect(body?.tabIndex).toBe(0)

    await user.tab()
    expect(document.activeElement).toBe(body)
    expect(screen.getByRole('tooltip')).toBeTruthy()

    await user.tab({ shift: true })
    expect(document.activeElement).toBe(trigger)
    expect(screen.getByRole('tooltip')).toBeTruthy()

    await user.tab()
    await user.tab()
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'After' }))
    expect(screen.queryByRole('tooltip')).toBeNull()
  })

  it('InfoTip_FittingBody_StaysOutOfTheTabOrder', async () => {
    const { user } = setup()
    await user.tab()
    await user.tab()
    expect(screen.getByRole('tooltip').querySelector('.info-tip-body')?.hasAttribute('tabindex')).toBe(false)
    await user.tab()
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'After' }))
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
