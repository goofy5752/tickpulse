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

beforeEach(() => {
  fetchMock = vi.fn<typeof fetch>((input, init) => {
    const url = new URL(urlOf(input), 'http://localhost')
    const method = init?.method ?? 'GET'
    if (url.pathname === '/api/symbols') return Promise.resolve(Response.json(symbols))
    if (url.pathname === '/api/admin/symbols' && method === 'POST') {
      return Promise.resolve(problem(422, 'invalid-symbol', 'Invalid symbol', 'FOOUSDT is not a trading symbol on Binance.'))
    }
    if (url.pathname === '/api/admin/symbols/BTCUSDT' && method === 'DELETE') {
      return Promise.resolve(new Response(null, { status: 204 }))
    }
    return Promise.resolve(new Response(null, { status: 404 }))
  })
  vi.stubGlobal('fetch', fetchMock)
})

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
})
