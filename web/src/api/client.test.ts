import { afterEach, describe, expect, it, vi } from 'vitest'
import { adminRequest, ApiError, apiPaths, apiUrl, getJson, getRaw, MissingApiKeyError } from './client'

function stubFetch(response: Response) {
  const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(response)
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function headersOf(fetchMock: ReturnType<typeof stubFetch>, call = 0): Headers {
  return new Headers(fetchMock.mock.calls[call]?.[1]?.headers)
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('getRaw', () => {
  it('getRaw_ReturnsContentTypeAndBody', async () => {
    const xml = '<averagePrice xmlns="https://tickpulse.dev/schema/v1"><symbol>BTCUSDT</symbol></averagePrice>'
    const fetchMock = stubFetch(
      new Response(xml, {
        status: 200,
        headers: { 'Content-Type': 'application/xml; charset=utf-8' },
      }),
    )

    const result = await getRaw('/api/BTCUSDT/24hAvgPrice', 'application/xml')

    expect(fetchMock).toHaveBeenCalledOnce()
    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/BTCUSDT/24hAvgPrice')
    expect(headersOf(fetchMock).get('Accept')).toBe('application/xml')
    expect(result.status).toBe(200)
    expect(result.contentType).toBe('application/xml; charset=utf-8')
    expect(result.body).toBe(xml)
  })

  it('getRaw_MeasuresDurationAcrossTheWholeResponse', async () => {
    stubFetch(new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } }))
    vi.spyOn(performance, 'now').mockReturnValueOnce(100).mockReturnValueOnce(142)

    const result = await getRaw('/api/symbols', 'application/json')

    expect(result.durationMs).toBe(42)
  })

  it('getRaw_NeverSendsApiKey', async () => {
    const fetchMock = stubFetch(new Response('{}', { status: 200 }))

    await getRaw('/api/symbols', 'application/json')

    expect(headersOf(fetchMock).has('X-Api-Key')).toBe(false)
  })

  it('getRaw_ReturnsProblemBodyWithoutThrowing', async () => {
    const problem = '{"type":"https://tickpulse.dev/problems/no-data","title":"No data","status":404}'
    stubFetch(
      new Response(problem, {
        status: 404,
        headers: { 'Content-Type': 'application/problem+json' },
      }),
    )

    const result = await getRaw('/api/BTCUSDT/24hAvgPrice', 'application/json')

    expect(result.status).toBe(404)
    expect(result.contentType).toBe('application/problem+json')
    expect(result.body).toBe(problem)
  })
})

describe('getJson', () => {
  it('getJson_ReturnsParsedBody', async () => {
    const fetchMock = stubFetch(Response.json([{ symbol: 'BTCUSDT', isActive: true }]))

    const body = await getJson<{ symbol: string }[]>('/api/symbols')

    expect(body).toEqual([{ symbol: 'BTCUSDT', isActive: true }])
    expect(headersOf(fetchMock).get('Accept')).toBe('application/json')
    expect(headersOf(fetchMock).has('X-Api-Key')).toBe(false)
  })

  it('getJson_ProblemResponse_ThrowsApiErrorWithProblem', async () => {
    stubFetch(
      new Response(
        JSON.stringify({
          type: 'https://tickpulse.dev/problems/unknown-symbol',
          title: 'Unknown symbol',
          status: 404,
          detail: 'Symbol FOOBAR is not monitored.',
          traceId: '00-abc-def-00',
        }),
        { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )

    const error = await getJson('/api/FOOBAR/24hAvgPrice').catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    const apiError = error as ApiError
    expect(apiError.status).toBe(404)
    expect(apiError.problem?.type).toBe('https://tickpulse.dev/problems/unknown-symbol')
    expect(apiError.message).toBe('Symbol FOOBAR is not monitored.')
  })

  it('getJson_NonProblemError_ThrowsApiErrorWithStatusText', async () => {
    stubFetch(new Response('upstream down', { status: 502, statusText: 'Bad Gateway' }))

    const error = await getJson('/api/symbols').catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(502)
    expect((error as ApiError).problem).toBeUndefined()
  })
})

describe('adminRequest', () => {
  it('adminRequest_SendsApiKeyAndJsonBody', async () => {
    const fetchMock = stubFetch(Response.json({ symbol: 'SOLUSDT' }, { status: 201 }))

    const result = await adminRequest<{ symbol: string }>('POST', '/api/admin/symbols', 'secret-key', {
      symbol: 'SOLUSDT',
    })

    const init = fetchMock.mock.calls[0]?.[1]
    expect(init?.method).toBe('POST')
    expect(init?.body).toBe('{"symbol":"SOLUSDT"}')
    const headers = headersOf(fetchMock)
    expect(headers.get('X-Api-Key')).toBe('secret-key')
    expect(headers.get('Content-Type')).toBe('application/json')
    expect(result).toEqual({ status: 201, data: { symbol: 'SOLUSDT' } })
  })

  it('adminRequest_NoContent_ReturnsUndefinedData', async () => {
    stubFetch(new Response(null, { status: 204 }))

    const result = await adminRequest('DELETE', '/api/admin/symbols/SOLUSDT', 'secret-key')

    expect(result).toEqual({ status: 204, data: undefined })
  })

  it.each([
    [401, 'unauthorized', 'The X-Api-Key header is missing or wrong.'],
    [403, 'admin-disabled', 'Admin endpoints are disabled because no API key is configured.'],
    [409, 'symbol-limit', 'At most 20 symbols can be active.'],
  ])('adminRequest_Problem%i_ThrowsApiErrorWithDetail', async (status, slug, detail) => {
    stubFetch(
      new Response(
        JSON.stringify({ type: `https://tickpulse.dev/problems/${slug}`, title: 'Rejected', status, detail }),
        { status, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )

    const error = await adminRequest('POST', '/api/admin/symbols', 'secret-key', { symbol: 'SOLUSDT' }).catch(
      (e: unknown) => e,
    )

    expect(error).toBeInstanceOf(ApiError)
    const apiError = error as ApiError
    expect(apiError.status).toBe(status)
    expect(apiError.problemSlug).toBe(slug)
    expect(apiError.message).toBe(detail)
  })

  it('adminRequest_ProblemWithTitleOnly_UsesTitleAsMessage', async () => {
    stubFetch(
      new Response(
        JSON.stringify({ type: 'https://tickpulse.dev/problems/invalid-symbol', title: 'Invalid symbol', status: 422 }),
        { status: 422, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )

    const error = await adminRequest('POST', '/api/admin/symbols', 'secret-key', { symbol: 'x' }).catch(
      (e: unknown) => e,
    )

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).message).toBe('Invalid symbol')
    expect((error as ApiError).problem?.detail).toBeUndefined()
  })

  it.each(['', '   '])('adminRequest_BlankApiKey_ThrowsWithoutFetching (%j)', async (apiKey) => {
    const fetchMock = stubFetch(new Response(null, { status: 204 }))

    await expect(adminRequest('DELETE', '/api/admin/symbols/SOLUSDT', apiKey)).rejects.toBeInstanceOf(
      MissingApiKeyError,
    )
    expect(fetchMock).not.toHaveBeenCalled()
  })
})

describe('apiUrl', () => {
  afterEach(() => {
    vi.unstubAllEnvs()
    vi.resetModules()
  })

  it('apiUrl_BaseWithTrailingSlash_DoesNotDoubleTheSlash', async () => {
    vi.stubEnv('VITE_API_BASE_URL', 'https://api.example.test/')
    vi.resetModules()

    const { apiUrl: freshApiUrl } = await import('./client')

    expect(freshApiUrl('/api/symbols')).toBe('https://api.example.test/api/symbols')
  })

  it('apiUrl_NoBase_IsSameOrigin', () => {
    expect(apiUrl('/hubs/market')).toBe('/hubs/market')
  })
})

describe('apiPaths', () => {
  it('apiPaths_Sma_EncodesStartOffset', () => {
    expect(apiPaths.sma('btcusdt', 20, '1d', '2026-10-01T03:00:00+03:00')).toBe(
      '/api/BTCUSDT/SimpleMovingAverage?n=20&p=1d&s=2026-10-01T03%3A00%3A00%2B03%3A00',
    )
    expect(apiPaths.sma('BTCUSDT', 5, '1m')).toBe('/api/BTCUSDT/SimpleMovingAverage?n=5&p=1m')
  })
})
