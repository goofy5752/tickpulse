/*
 * Development-only market simulation (VITE_MOCK=1 with `npm run dev`). It never reaches a
 * production build: main.tsx imports it behind `import.meta.env.DEV`, and vite.config.ts only
 * aliases the fake hub client while serving.
 *
 * Three symbols follow seeded random walks per period, anchored so the newest close is the live
 * price. The live price keeps moving on a timer and updates the open bucket of every period.
 * Some buckets are left out on purpose (an outage on ETH's daily series, thin ADA minutes) so
 * the dashboard's sparse-data paths get exercised.
 */
import {
  PERIODS,
  type AveragePriceResponse,
  type BackfillStatus,
  type CandleResponse,
  type LivePrice,
  type Period,
  type SmaResponse,
  type SymbolResponse,
  type SystemStatsResponse,
} from '@/api/types'
import { PERIOD_SECONDS } from '@/lib/sma'

const WEEK_ORIGIN = Date.UTC(2000, 0, 3) / 1000

function alignDown(t: number, length: number): number {
  return WEEK_ORIGIN + Math.floor((t - WEEK_ORIGIN) / length) * length
}

function alignUp(t: number, length: number): number {
  const down = alignDown(t, length)
  return down === t ? t : down + length
}

const iso = (seconds: number) => new Date(seconds * 1000).toISOString().replace('.000Z', 'Z')
const nowSeconds = () => Math.floor(Date.now() / 1000)

function seeded(seed: number): () => number {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

function gaussian(rand: () => number): number {
  const u = Math.max(rand(), 1e-12)
  return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * rand())
}

interface Bar {
  t: number
  o: number
  h: number
  l: number
  c: number
  v: number
}

interface Spec {
  symbol: string
  price: number
  decimals: number
  /** Log-return standard deviation per minute. */
  sigma: number
  /** Target 24h change in percent; the walk is bent to land on it. */
  change24h: number
  ticksPerSecond: number
  volumePerMinute: number
  seed: number
  /** Buckets removed from a period: `back` buckets before the newest, `length` long. */
  outages?: { period: Period; back: number; length: number }[]
  /** Share of 1m buckets with no trades. */
  thinMinutes?: number
}

const SPECS: Spec[] = [
  { symbol: 'BTCUSDT', price: 97_412.3, decimals: 2, sigma: 0.00052, change24h: 1.84, ticksPerSecond: 21.4, volumePerMinute: 9.5, seed: 11 },
  {
    symbol: 'ETHUSDT',
    price: 3_611.08,
    decimals: 2,
    sigma: 0.00068,
    change24h: -0.42,
    ticksPerSecond: 14.9,
    volumePerMinute: 140,
    seed: 23,
    outages: [{ period: '1d', back: 47, length: 3 }],
  },
  {
    symbol: 'ADAUSDT',
    price: 0.7214,
    decimals: 4,
    sigma: 0.0009,
    change24h: 3.1,
    ticksPerSecond: 4.9,
    volumePerMinute: 95_000,
    seed: 37,
    thinMinutes: 0.04,
  },
]

/**
 * Pairs the simulated exchange lists as trading, so the admin panel can add them. Anything else
 * is "not a trading symbol on Binance" (422).
 */
const CATALOG: Spec[] = [
  { symbol: 'SOLUSDT', price: 182.41, decimals: 2, sigma: 0.0008, change24h: 2.6, ticksPerSecond: 9.2, volumePerMinute: 600, seed: 41 },
  { symbol: 'BNBUSDT', price: 642.1, decimals: 2, sigma: 0.0006, change24h: -0.8, ticksPerSecond: 6.1, volumePerMinute: 90, seed: 43 },
  { symbol: 'XRPUSDT', price: 2.3114, decimals: 4, sigma: 0.0009, change24h: 1.2, ticksPerSecond: 8.4, volumePerMinute: 200_000, seed: 47 },
  { symbol: 'DOGEUSDT', price: 0.38121, decimals: 5, sigma: 0.001, change24h: -2.1, ticksPerSecond: 7.3, volumePerMinute: 900_000, seed: 53 },
  { symbol: 'LINKUSDT', price: 23.47, decimals: 3, sigma: 0.0008, change24h: 0.6, ticksPerSecond: 3.8, volumePerMinute: 3_000, seed: 59 },
  { symbol: 'AVAXUSDT', price: 38.92, decimals: 2, sigma: 0.0009, change24h: -1.4, ticksPerSecond: 3.6, volumePerMinute: 2_500, seed: 61 },
  { symbol: 'LTCUSDT', price: 104.6, decimals: 2, sigma: 0.0007, change24h: 0.3, ticksPerSecond: 2.9, volumePerMinute: 800, seed: 67 },
]

/**
 * Lower than the real default of 20 on purpose, so the 409 `symbol-limit` answer is reachable
 * after adding a few symbols.
 */
export const MOCK_MAX_ACTIVE = 6

/** A new symbol's backfill: queued for this long, then running until BACKFILL_DONE_MS. */
const BACKFILL_PENDING_MS = 3_000
const BACKFILL_DONE_MS = 11_000

const SYMBOL_FORMAT = /^[A-Z0-9]{5,20}$/

export type AddOutcome =
  | { kind: 'created' | 'already'; symbol: SymbolResponse }
  | { kind: 'invalid-format' | 'not-trading'; symbol: string }
  | { kind: 'limit'; max: number }

/** Buckets generated per period: 25 h of minutes, then days of backfill depth and half a year of weeks. */
const DEPTH: Record<Period, number> = { '1m': 1500, '5m': 1000, '30m': 1000, '1d': 182, '1w': 27 }

class SymbolSim {
  readonly spec: Spec
  price: number
  liveTicks = 0
  lastTrade: number
  private readonly series = new Map<Period, Bar[]>()
  private readonly rand: () => number

  constructor(spec: Spec, now: number) {
    this.spec = spec
    this.price = spec.price
    this.lastTrade = now
    this.rand = seeded(spec.seed)
    for (const period of PERIODS) this.series.set(period, this.generate(period, now))
  }

  private round(value: number): number {
    const f = 10 ** this.spec.decimals
    return Math.round(value * f) / f
  }

  private generate(period: Period, now: number): Bar[] {
    const length = PERIOD_SECONDS[period]
    const count = DEPTH[period]
    const step = this.spec.sigma * Math.sqrt(length / 60)
    const newest = alignDown(now, length)

    // Log returns into each bucket, with a slow swell so the chart has shape, not just noise.
    const returns = Array.from({ length: count }, (_, k) => {
      const swell = Math.sin((k / count) * Math.PI * 3.2 + this.spec.seed) * step * 0.35
      return gaussian(this.rand) * step + swell
    })
    const per24h = Math.round(86_400 / length)
    if (per24h >= 1 && per24h < count) {
      let sum = 0
      for (let k = count - per24h; k < count; k++) sum += returns[k] ?? 0
      const correction = (Math.log(1 + this.spec.change24h / 100) - sum) / per24h
      for (let k = count - per24h; k < count; k++) returns[k] = (returns[k] ?? 0) + correction
    }

    const closes = new Array<number>(count)
    closes[count - 1] = this.price
    for (let k = count - 1; k > 0; k--) closes[k - 1] = (closes[k] ?? 0) / Math.exp(returns[k] ?? 0)

    const bars: Bar[] = []
    for (let k = 0; k < count; k++) {
      const c = closes[k] ?? this.price
      const o = k > 0 ? (closes[k - 1] ?? c) : c / Math.exp(gaussian(this.rand) * step)
      const wick = () => Math.abs(gaussian(this.rand)) * step * 0.45
      bars.push({
        t: newest - (count - 1 - k) * length,
        o: this.round(o),
        h: this.round(Math.max(o, c) * (1 + wick())),
        l: this.round(Math.min(o, c) * (1 - wick())),
        c: this.round(c),
        v: this.spec.volumePerMinute * (length / 60) * (0.55 + this.rand() * 0.9),
      })
    }

    const removed = new Set<number>()
    for (const outage of this.spec.outages ?? []) {
      if (outage.period !== period) continue
      for (let i = 0; i < outage.length; i++) removed.add(count - 1 - outage.back - i)
    }
    if (period === '1m' && this.spec.thinMinutes) {
      for (let k = 0; k < count - 1; k++) if (this.rand() < (this.spec.thinMinutes ?? 0)) removed.add(k)
    }
    return bars.filter((_, k) => !removed.has(k))
  }

  bars(period: Period): Bar[] {
    return this.series.get(period) ?? []
  }

  /** One trade: move the price and fold it into the open bucket of every period. */
  trade(now: number, rand: () => number) {
    const move = gaussian(rand) * this.spec.sigma * 0.16 + (this.spec.price - this.price) / this.spec.price * 0.002
    this.price = Math.max(10 ** -this.spec.decimals, this.round(this.price * Math.exp(move)))
    this.liveTicks += 1
    this.lastTrade = now

    for (const period of PERIODS) {
      const bars = this.bars(period)
      const bucket = alignDown(now, PERIOD_SECONDS[period])
      const last = bars.at(-1)
      if (last?.t === bucket) {
        last.h = Math.max(last.h, this.price)
        last.l = Math.min(last.l, this.price)
        last.c = this.price
        last.v += this.spec.volumePerMinute / 60
      } else {
        bars.push({ t: bucket, o: last?.c ?? this.price, h: this.price, l: this.price, c: this.price, v: 0 })
      }
    }
  }

  /** Close of the 30m bucket 24 h back. */
  priceDayAgo(now: number): number | null {
    const target = alignDown(now - 86_400, PERIOD_SECONDS['30m'])
    const bar = this.bars('30m').find((b) => b.t >= target)
    return bar?.c ?? null
  }
}

type PriceListener = (price: LivePrice) => void

interface Entry {
  sim: SymbolSim
  active: boolean
  /** When the backfill was queued (ms); null for the seeded symbols, whose history is complete. */
  backfillQueuedAt: number | null
}

class MarketSim {
  private readonly entries: Map<string, Entry>
  private readonly listeners = new Set<PriceListener>()
  private readonly rand = seeded(97)
  private readonly startedAt = nowSeconds()
  private timer: ReturnType<typeof setTimeout> | undefined

  constructor() {
    const now = nowSeconds()
    this.entries = new Map(
      SPECS.map((spec) => [spec.symbol, { sim: new SymbolSim(spec, now), active: true, backfillQueuedAt: null }]),
    )
  }

  private get(symbol: string): SymbolSim | undefined {
    this.ensureRunning()
    return this.entries.get(symbol.toUpperCase())?.sim
  }

  private ensureRunning() {
    if (this.timer !== undefined) return
    const loop = () => {
      const now = nowSeconds()
      for (const { sim, active } of this.entries.values()) {
        // A removed symbol keeps its history but no longer receives trades.
        if (!active) continue
        // Busier symbols trade more often; each emitted trade stands for a burst of real ones.
        if (this.rand() < Math.min(0.85, sim.spec.ticksPerSecond / 28)) {
          sim.trade(now, this.rand)
          const update: LivePrice = { symbol: sim.spec.symbol, price: sim.price, tradeTime: new Date().toISOString() }
          for (const listener of this.listeners) listener(update)
        }
      }
      this.timer = setTimeout(loop, 450 + this.rand() * 650)
    }
    this.timer = setTimeout(loop, 600)
  }

  onPrice(listener: PriceListener): () => void {
    this.ensureRunning()
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }

  has(symbol: string): boolean {
    return this.get(symbol) !== undefined
  }

  private backfillStatus(entry: Entry): BackfillStatus {
    if (entry.backfillQueuedAt === null) return 'Completed'
    const elapsed = Date.now() - entry.backfillQueuedAt
    if (elapsed < BACKFILL_PENDING_MS) return 'Pending'
    return elapsed < BACKFILL_DONE_MS ? 'Running' : 'Completed'
  }

  private describe(entry: Entry): SymbolResponse {
    const sim = entry.sim
    const dayAgo = sim.priceDayAgo(nowSeconds())
    return {
      symbol: sim.spec.symbol,
      isActive: entry.active,
      latestPrice: sim.price,
      change24hPercent: dayAgo ? Math.round(((sim.price - dayAgo) / dayAgo) * 10_000) / 100 : null,
      backfillStatus: this.backfillStatus(entry),
    }
  }

  private activeCount(): number {
    let count = 0
    for (const entry of this.entries.values()) if (entry.active) count++
    return count
  }

  /** Every symbol ever monitored, removed ones included (as the real list does). */
  symbols(): SymbolResponse[] {
    this.ensureRunning()
    return [...this.entries.values()].map((entry) => this.describe(entry))
  }

  /** The admin add flow, in the real order: format, already active, limit, exchange, add. */
  addSymbol(raw: string): AddOutcome {
    this.ensureRunning()
    const symbol = raw.trim().toUpperCase()
    if (!SYMBOL_FORMAT.test(symbol)) return { kind: 'invalid-format', symbol: raw }
    const existing = this.entries.get(symbol)
    if (existing?.active) return { kind: 'already', symbol: this.describe(existing) }
    if (this.activeCount() >= MOCK_MAX_ACTIVE) return { kind: 'limit', max: MOCK_MAX_ACTIVE }
    const spec = CATALOG.find((listed) => listed.symbol === symbol) ?? SPECS.find((seeded) => seeded.symbol === symbol)
    if (!spec) return { kind: 'not-trading', symbol }
    const entry: Entry = existing ?? { sim: new SymbolSim(spec, nowSeconds()), active: true, backfillQueuedAt: null }
    entry.active = true
    entry.backfillQueuedAt = Date.now()
    this.entries.set(symbol, entry)
    return { kind: 'created', symbol: this.describe(entry) }
  }

  /** Stops collecting; the history stays queryable. False when the symbol is not active. */
  removeSymbol(raw: string): boolean {
    const entry = this.entries.get(raw.trim().toUpperCase())
    if (!entry?.active) return false
    entry.active = false
    return true
  }

  average(symbol: string): AveragePriceResponse | null {
    const sim = this.get(symbol)
    if (!sim) return null
    const now = nowSeconds()
    const from = now - 86_400
    const minutes = sim.bars('1m').filter((b) => b.t >= alignDown(from, 60))
    if (minutes.length === 0) return null
    const mean = minutes.reduce((sum, b) => sum + (b.o + b.h + b.l + b.c) / 4, 0) / minutes.length
    const coveredSeconds = Math.min(86_400, minutes.length * 60) - 120
    return {
      symbol: sim.spec.symbol,
      averagePrice: Math.round(mean * 1e8) / 1e8,
      from: iso(from),
      to: iso(now),
      coveredSeconds,
      sampleCount: Math.round(coveredSeconds * sim.spec.ticksPerSecond) + sim.liveTicks * 28,
      windowHours: 24,
    }
  }

  sma(symbol: string, n: number, p: Period, s: number | undefined): SmaResponse | 'no-data' | null {
    const sim = this.get(symbol)
    if (!sim) return null
    const length = PERIOD_SECONDS[p]
    const now = nowSeconds()
    const current = alignDown(now, length)
    const start = s === undefined ? current - n * length : alignUp(s, length)
    const end = s === undefined ? current : Math.min(start + n * length, current)
    if (start >= end) return 'no-data'
    const points = sim.bars(p).filter((b) => b.t >= start && b.t < end)
    if (points.length === 0) return 'no-data'
    const value = points.reduce((sum, b) => sum + b.c, 0) / points.length
    return {
      symbol: sim.spec.symbol,
      period: p,
      requestedDataPoints: n,
      actualDataPoints: points.length,
      value: Math.round(value * 1e8) / 1e8,
      windowStart: iso(start),
      windowEnd: iso(end),
      referenceTime: iso(now),
      startParameter: s === undefined ? null : iso(s),
      dataPoints: points.map((b) => ({ bucketStart: iso(b.t), close: b.c })),
    }
  }

  candles(symbol: string, p: Period, limit: number): CandleResponse[] | null {
    const sim = this.get(symbol)
    if (!sim) return null
    const length = PERIOD_SECONDS[p]
    const oldest = alignDown(nowSeconds(), length) - (limit - 1) * length
    return sim
      .bars(p)
      .filter((b) => b.t >= oldest)
      .map((b) => ({
        bucketStart: iso(b.t),
        open: b.o,
        high: b.h,
        low: b.l,
        close: b.c,
        volume: Math.round(b.v * 1e4) / 1e4,
      }))
  }

  stats(): SystemStatsResponse {
    this.ensureRunning()
    const now = nowSeconds()
    const elapsed = now - this.startedAt
    const active = [...this.entries.values()].filter((entry) => entry.active)
    const rate = active.reduce((sum, entry) => sum + entry.sim.spec.ticksPerSecond, 0)
    const reconnect = this.startedAt - 3 * 3600 - 12 * 60
    const spike = this.rand() < 0.03
    return {
      wsStatus: 'Connected',
      isLeader: true,
      ticksPerSecond: Math.round(rate * (0.82 + this.rand() * 0.36) * 10) / 10,
      ingestLagMs: Math.round(spike ? 180 + this.rand() * 260 : 24 + this.rand() * 32),
      storedTicksApprox: Math.round(8_412_000 + elapsed * rate),
      uptimeSeconds: 2 * 86_400 + 4 * 3600 + elapsed,
      connectedSince: iso(reconnect),
      lastReconnect: iso(reconnect),
      nextRotation: iso(reconnect + 23 * 3600),
      activeSymbols: active.length,
      version: '0.1.0-dev',
    }
  }
}

export const market = new MarketSim()
