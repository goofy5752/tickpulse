import { useMemo, useState } from 'react'
import { useStats, useSymbols } from '@/api/queries'
import { useMarketHub } from '@/hooks/useMarketHub'
import { DEFAULT_SMA_PARAMS, type SmaParams } from '@/lib/smaParams'
import { AnalyticsPanel } from '@/sections/AnalyticsPanel'
import { ChartPanel } from '@/sections/ChartPanel'
import { Nav } from '@/sections/Nav'
import { TickerTiles } from '@/sections/TickerTiles'

const PREFERRED_SYMBOL = 'BTCUSDT'

export function App() {
  const symbols = useSymbols()
  const active = useMemo(() => (symbols.data ?? []).filter((s) => s.isActive), [symbols.data])
  const activeNames = useMemo(() => active.map((s) => s.symbol), [active])

  const hub = useMarketHub(activeNames)
  // The hub pushes stats every 2 s; poll over HTTP only while it is not connected.
  const fallbackStats = useStats({ enabled: hub.status !== 'connected' })
  const stats = hub.status === 'connected' ? hub.stats : (fallbackStats.data ?? null)

  const [picked, setPicked] = useState<string | null>(null)
  const symbol =
    picked !== null && activeNames.includes(picked)
      ? picked
      : activeNames.includes(PREFERRED_SYMBOL)
        ? PREFERRED_SYMBOL
        : activeNames[0]
  const [params, setParams] = useState<SmaParams>(DEFAULT_SMA_PARAMS)

  const latestPrice = active.find((s) => s.symbol === symbol)?.latestPrice ?? null

  return (
    <div id="top" className="mx-auto flex min-h-dvh w-full max-w-[1320px] flex-col gap-5 px-4 py-3 sm:px-6 lg:px-8">
      <a
        href="#main"
        className="sr-only rounded-full bg-glass-strong px-4 py-2 focus:not-sr-only focus:fixed focus:top-4 focus:left-4 focus:z-50"
      >
        Skip to content
      </a>
      <Nav hubStatus={hub.status} stats={stats} />
      <main id="main" className="flex flex-col gap-5 pb-10">
        <h1 className="sr-only">TickPulse market dashboard</h1>
        <TickerTiles
          symbols={symbols.data ? active : undefined}
          isLoading={symbols.isPending}
          error={symbols.error}
          prices={hub.prices}
          selected={symbol}
          onSelect={setPicked}
        />
        <div className="grid grid-cols-1 items-stretch gap-5 lg:grid-cols-[minmax(0,1fr)_22.5rem]">
          <ChartPanel
            symbol={symbol}
            params={params}
            onParamsChange={setParams}
            live={symbol ? hub.prices[symbol] : undefined}
            fallbackPrice={latestPrice}
          />
          <AnalyticsPanel symbol={symbol} params={params} onParamsChange={setParams} />
        </div>
      </main>
    </div>
  )
}
