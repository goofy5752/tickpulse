import { QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createQueryClient } from '@/api/queries'
import type { ProblemResponse, SymbolResponse } from '@/api/types'
import { ADMIN_KEY_STORAGE_KEY, AdminSymbols } from './AdminSymbols'

const symbols: SymbolResponse[] = [
  { symbol: 'BTCUSDT', isActive: true, latestPrice: 97_412.3, change24hPercent: 1.84, backfillStatus: 'Completed' },
  { symbol: 'XRPUSDT', isActive: false, latestPrice: 2.31, change24hPercent: null, backfillStatus: 'Completed' },
]

function urlOf(input: string | URL | Request): string {
  if (typeof input === 'string') return input
  return input instanceof URL ? input.href : input.url
}

function problem(status: number, slug: string, title: string, detail: string): Response {
  const body: ProblemResponse = { type: `https://tickpulse.dev/problems/${slug}`, title, status, detail }
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/problem+json' } })
}

let fetchMock: ReturnType<typeof vi.fn<typeof fetch>>
/** What POST and DELETE answer; tests swap these to reach each status. */
let answerPost: () => Promise<Response>
let answerDelete: () => Promise<Response>

beforeEach(() => {
  answerPost = () =>
    Promise.resolve(problem(422, 'invalid-symbol', 'Invalid symbol', 'FOOUSDT is not a trading symbol on Binance.'))
  answerDelete = () => Promise.resolve(new Response(null, { status: 204 }))
  fetchMock = vi.fn<typeof fetch>((input, init) => {
    const url = new URL(urlOf(input), 'http://localhost')
    const method = init?.method ?? 'GET'
    if (url.pathname === '/api/symbols') return Promise.resolve(Response.json(symbols))
    if (url.pathname === '/api/admin/symbols' && method === 'POST') return answerPost()
    if (url.pathname === '/api/admin/symbols/BTCUSDT' && method === 'DELETE') return answerDelete()
    return Promise.resolve(new Response(null, { status: 404 }))
  })
  vi.stubGlobal('fetch', fetchMock)
})

async function addSymbol(user: ReturnType<typeof userEvent.setup>, symbol: string) {
  await user.type(screen.getByLabelText(/symbol to add/i), symbol)
  await user.click(screen.getByRole('button', { name: /^add symbol$/i }))
  return screen.findByRole('alert')
}

async function openConfirm(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: 'Remove BTCUSDT' }))
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  sessionStorage.clear()
  localStorage.clear()
})

function renderAdmin() {
  return render(
    <QueryClientProvider client={createQueryClient()}>
      <AdminSymbols />
    </QueryClientProvider>,
  )
}

function adminCalls(method: string) {
  return fetchMock.mock.calls.filter(([input, init]) => {
    const url = new URL(urlOf(input), 'http://localhost')
    return url.pathname.startsWith('/api/admin/') && (init?.method ?? 'GET') === method
  })
}

describe('AdminSymbols', () => {
  it('Admin_AddError_ShowsProblemDetail', async () => {
    sessionStorage.setItem(ADMIN_KEY_STORAGE_KEY, 'k-123')
    const user = userEvent.setup()
    renderAdmin()

    await user.type(screen.getByLabelText(/symbol to add/i), 'fooUSDT')
    await user.click(screen.getByRole('button', { name: /^add symbol$/i }))

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain('FOOUSDT is not a trading symbol on Binance.')
    // The slug also gets a plain-language headline.
    expect(alert.textContent).toMatch(/cannot be monitored/i)

    const [call] = adminCalls('POST')
    expect(new Headers(call?.[1]?.headers).get('X-Api-Key')).toBe('k-123')
    expect(JSON.parse(call?.[1]?.body as string)).toEqual({ symbol: 'FOOUSDT' })
  })

  it('Admin_KeyKeptInSessionStorage', async () => {
    const user = userEvent.setup()
    const first = renderAdmin()

    await user.type(screen.getByLabelText(/admin api key/i), 's3cret')
    expect(sessionStorage.getItem(ADMIN_KEY_STORAGE_KEY)).toBe('s3cret')
    expect(localStorage.getItem(ADMIN_KEY_STORAGE_KEY)).toBeNull()

    first.unmount()
    renderAdmin()
    expect(screen.getByLabelText<HTMLInputElement>(/admin api key/i).value).toBe('s3cret')

    await user.click(screen.getByRole('button', { name: /forget key/i }))
    expect(sessionStorage.getItem(ADMIN_KEY_STORAGE_KEY)).toBeNull()
  })

  it('Admin_NoKey_AsksForKey_SendsNothing', async () => {
    const user = userEvent.setup()
    renderAdmin()

    await user.type(screen.getByLabelText(/symbol to add/i), 'SOLUSDT')
    await user.click(screen.getByRole('button', { name: /^add symbol$/i }))

    expect((await screen.findByRole('alert')).textContent).toMatch(/enter the admin api key first/i)
    expect(adminCalls('POST')).toHaveLength(0)
    expect(document.activeElement).toBe(screen.getByLabelText(/admin api key/i))
  })

  it('Admin_StorageUnavailable_StillUsable', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked')
    })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked')
    })
    const user = userEvent.setup()
    renderAdmin()

    const input = screen.getByLabelText<HTMLInputElement>(/admin api key/i)
    await user.type(input, 'abc')
    expect(input.value).toBe('abc')
  })

  it('Admin_Remove_ConfirmsInUi_ThenDeletes', async () => {
    const confirmSpy = vi.fn(() => true)
    vi.stubGlobal('confirm', confirmSpy)
    sessionStorage.setItem(ADMIN_KEY_STORAGE_KEY, 'k-123')
    const user = userEvent.setup()
    renderAdmin()

    // Only active symbols are listed.
    await user.click(await screen.findByRole('button', { name: 'Remove BTCUSDT' }))
    expect(screen.queryByRole('button', { name: 'Remove XRPUSDT' })).toBeNull()
    expect(adminCalls('DELETE')).toHaveLength(0)
    expect(screen.getByText(/stop monitoring btcusdt\?/i)).toBeTruthy()

    await user.click(screen.getByRole('button', { name: /^yes, remove$/i }))

    await waitFor(() => {
      expect(adminCalls('DELETE')).toHaveLength(1)
    })
    expect((await screen.findByRole('status')).textContent).toMatch(/stopped monitoring btcusdt/i)
    expect(confirmSpy).not.toHaveBeenCalled()
  })

  it('Admin_Remove_KeepCancels', async () => {
    sessionStorage.setItem(ADMIN_KEY_STORAGE_KEY, 'k-123')
    const user = userEvent.setup()
    renderAdmin()

    await user.click(await screen.findByRole('button', { name: 'Remove BTCUSDT' }))
    await user.click(screen.getByRole('button', { name: /^keep$/i }))

    expect(screen.queryByText(/stop monitoring btcusdt\?/i)).toBeNull()
    expect(adminCalls('DELETE')).toHaveLength(0)
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Remove BTCUSDT' }))
  })

  it('Admin_Add401_SaysKeyRejected_FocusesKey', async () => {
    sessionStorage.setItem(ADMIN_KEY_STORAGE_KEY, 'wrong')
    answerPost = () =>
      Promise.resolve(problem(401, 'unauthorized', 'Unauthorized', 'The API key is not valid.'))
    const user = userEvent.setup()
    renderAdmin()

    const alert = await addSymbol(user, 'SOLUSDT')
    expect(alert.textContent).toMatch(/the api key was not accepted/i)
    expect(alert.textContent).toContain('The API key is not valid.')
    expect(document.activeElement).toBe(screen.getByLabelText(/admin api key/i))
  })

  it('Admin_Add403_SaysAdminDisabled', async () => {
    sessionStorage.setItem(ADMIN_KEY_STORAGE_KEY, 'k-123')
    answerPost = () =>
      Promise.resolve(problem(403, 'admin-disabled', 'Admin disabled', 'No admin API key is configured.'))
    const user = userEvent.setup()
    renderAdmin()

    const alert = await addSymbol(user, 'SOLUSDT')
    expect(alert.textContent).toMatch(/symbol management is turned off on this server/i)
    expect(alert.textContent).toContain('No admin API key is configured.')
  })

  it('Admin_Add409_SaysLimitReached', async () => {
    sessionStorage.setItem(ADMIN_KEY_STORAGE_KEY, 'k-123')
    answerPost = () =>
      Promise.resolve(problem(409, 'symbol-limit', 'Symbol limit reached', 'At most 20 symbols can be active at once.'))
    const user = userEvent.setup()
    renderAdmin()

    const alert = await addSymbol(user, 'SOLUSDT')
    expect(alert.textContent).toMatch(/the symbol limit is reached/i)
    expect(alert.textContent).toContain('At most 20 symbols can be active at once.')
  })

  it('Admin_Confirm_FocusesKeep_EscapeCancels', async () => {
    sessionStorage.setItem(ADMIN_KEY_STORAGE_KEY, 'k-123')
    const user = userEvent.setup()
    renderAdmin()

    await openConfirm(user)
    expect(document.activeElement).toBe(screen.getByRole('button', { name: /^keep$/i }))

    await user.keyboard('{Escape}')
    expect(screen.queryByText(/stop monitoring btcusdt?/i)).toBeNull()
    expect(adminCalls('DELETE')).toHaveLength(0)
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Remove BTCUSDT' }))
  })

  it('Admin_RemoveSucceeds_FocusMovesToAddField', async () => {
    sessionStorage.setItem(ADMIN_KEY_STORAGE_KEY, 'k-123')
    const user = userEvent.setup()
    renderAdmin()

    await openConfirm(user)
    await user.click(screen.getByRole('button', { name: /^yes, remove$/i }))

    await waitFor(() => {
      expect(document.activeElement).toBe(screen.getByLabelText(/symbol to add/i))
    })
  })

  it('Admin_RemoveFails_FocusReturnsToRow', async () => {
    sessionStorage.setItem(ADMIN_KEY_STORAGE_KEY, 'k-123')
    answerDelete = () =>
      Promise.resolve(problem(403, 'admin-disabled', 'Admin disabled', 'No admin API key is configured.'))
    const user = userEvent.setup()
    renderAdmin()

    await openConfirm(user)
    await user.click(screen.getByRole('button', { name: /^yes, remove$/i }))

    expect((await screen.findByRole('alert')).textContent).toMatch(/turned off/i)
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Remove BTCUSDT' }))
  })

  it('Admin_Remove401_FocusesKey', async () => {
    sessionStorage.setItem(ADMIN_KEY_STORAGE_KEY, 'wrong')
    answerDelete = () => Promise.resolve(problem(401, 'unauthorized', 'Unauthorized', 'The API key is not valid.'))
    const user = userEvent.setup()
    renderAdmin()

    await openConfirm(user)
    await user.click(screen.getByRole('button', { name: /^yes, remove$/i }))

    expect((await screen.findByRole('alert')).textContent).toMatch(/not accepted/i)
    expect(document.activeElement).toBe(screen.getByLabelText(/admin api key/i))
  })

  it('Admin_RemovePending_KeepsFocus_IgnoresRepeatAndEscape', async () => {
    sessionStorage.setItem(ADMIN_KEY_STORAGE_KEY, 'k-123')
    let finish: (response: Response) => void = () => undefined
    answerDelete = () =>
      new Promise<Response>((resolve) => {
        finish = resolve
      })
    const user = userEvent.setup()
    renderAdmin()

    await openConfirm(user)
    await user.click(screen.getByRole('button', { name: /^yes, remove$/i }))
    const pending = await screen.findByRole('button', { name: /removing/i })

    // Still focused (aria-disabled, not disabled), and neither a second click nor Escape acts.
    expect(document.activeElement).toBe(pending)
    expect(pending.getAttribute('aria-disabled')).toBe('true')
    expect(pending.hasAttribute('disabled')).toBe(false)
    await user.click(pending)
    await user.keyboard('{Escape}')
    expect(adminCalls('DELETE')).toHaveLength(1)
    expect(screen.getByText(/stop monitoring btcusdt?/i)).toBeTruthy()

    finish(new Response(null, { status: 204 }))
    await waitFor(() => {
      expect(document.activeElement).toBe(screen.getByLabelText(/symbol to add/i))
    })
  })
})
