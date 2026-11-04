import { QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createQueryClient } from '@/api/queries'
import { ApiPlayground } from './ApiPlayground'

const XML_BODY =
  '<AveragePriceResponse xmlns="https://tickpulse.dev/schema/v1" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">' +
  '<Symbol>BTCUSDT</Symbol><AveragePrice>96880.14</AveragePrice></AveragePriceResponse>'
const JSON_BODY = '{"symbol":"BTCUSDT","averagePrice":96880.14}'

function urlOf(input: string | URL | Request): string {
  if (typeof input === 'string') return input
  return input instanceof URL ? input.href : input.url
}

let fetchMock: ReturnType<typeof vi.fn<typeof fetch>>

beforeEach(() => {
  fetchMock = vi.fn<typeof fetch>((_input, init) => {
    const accept = new Headers(init?.headers).get('Accept')
    if (accept === 'application/xml') {
      return Promise.resolve(
        new Response(XML_BODY, { status: 200, headers: { 'Content-Type': 'application/xml; charset=utf-8' } }),
      )
    }
    return Promise.resolve(
      new Response(JSON_BODY, { status: 200, headers: { 'Content-Type': 'application/json; charset=utf-8' } }),
    )
  })
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function renderPlayground() {
  return render(
    <QueryClientProvider client={createQueryClient()}>
      <ApiPlayground symbol="BTCUSDT" symbols={['BTCUSDT', 'ETHUSDT']} />
    </QueryClientProvider>,
  )
}

function curlText(): string {
  const code = screen.getByText((_, element) => element?.tagName === 'CODE' && element.textContent.startsWith('curl '))
  return code.textContent
}

function lastRequest() {
  const call = fetchMock.mock.calls.at(-1)
  return {
    url: new URL(urlOf(call?.[0] ?? ''), 'http://localhost'),
    accept: new Headers(call?.[1]?.headers).get('Accept'),
  }
}

describe('ApiPlayground', () => {
  it('Playground_XmlToggle_SendsAcceptXml_ShowsRaw', async () => {
    const user = userEvent.setup()
    renderPlayground()

    await user.click(screen.getByLabelText('application/xml'))
    await user.click(screen.getByRole('button', { name: /send request/i }))

    await waitFor(() => {
      expect(lastRequest().accept).toBe('application/xml')
    })
    expect(lastRequest().url.pathname).toBe('/api/BTCUSDT/24hAvgPrice')

    const body = await screen.findByRole('region', { name: /response body/i })
    await waitFor(() => {
      expect(body.textContent).toContain('<AveragePrice>96880.14</AveragePrice>')
    })
    // Pretty-printed: one element per line, indented under the root.
    expect(body.textContent).toContain('\n  <Symbol>BTCUSDT</Symbol>')
    expect(screen.getByText('application/xml; charset=utf-8')).toBeTruthy()
    expect(screen.getByText('200 OK')).toBeTruthy()
  })

  it('Playground_CurlIncludesAcceptHeader', async () => {
    const user = userEvent.setup()
    renderPlayground()

    expect(curlText()).toContain('-H "Accept: application/json"')
    expect(curlText()).toContain('/api/BTCUSDT/24hAvgPrice')

    await user.click(screen.getByLabelText('application/xml'))
    expect(curlText()).toContain('-H "Accept: application/xml"')

    await user.click(screen.getByLabelText('Moving average'))
    expect(curlText()).toMatch(/\/api\/BTCUSDT\/SimpleMovingAverage\?n=20&p=1d/)
  })
})
