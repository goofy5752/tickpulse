// Mirrors the TickPulse.Contracts DTOs as they appear on the wire.
// JSON is camelCase, timestamps are ISO-8601 UTC strings ending in "Z", and C# decimals arrive as
// JSON numbers. Display code formats them; nothing in the dashboard does arithmetic that needs
// more precision than a double offers.

/** An ISO-8601 UTC timestamp, e.g. "2026-11-03T00:00:00Z". */
export type IsoDateTime = string

/** Bucket widths accepted by the SMA and candles endpoints. */
export const PERIODS = ['1m', '5m', '30m', '1d', '1w'] as const
export type Period = (typeof PERIODS)[number]

export function isPeriod(value: string): value is Period {
  return (PERIODS as readonly string[]).includes(value)
}

/** `GET /api/{symbol}/24hAvgPrice` */
export interface AveragePriceResponse {
  symbol: string
  averagePrice: number
  from: IsoDateTime
  to: IsoDateTime
  coveredSeconds: number
  sampleCount: number
  windowHours: number
}

export interface SmaDataPoint {
  bucketStart: IsoDateTime
  close: number
}

/** `GET /api/{symbol}/SimpleMovingAverage?n=&p=&s=` */
export interface SmaResponse {
  symbol: string
  period: Period
  requestedDataPoints: number
  actualDataPoints: number
  value: number
  windowStart: IsoDateTime
  windowEnd: IsoDateTime
  referenceTime: IsoDateTime
  startParameter: IsoDateTime | null
  dataPoints: SmaDataPoint[]
}

export type BackfillStatus = 'NotRequested' | 'Pending' | 'Running' | 'Completed' | 'Failed'

/** One entry of `GET /api/symbols`. */
export interface SymbolResponse {
  symbol: string
  isActive: boolean
  latestPrice: number | null
  change24hPercent: number | null
  backfillStatus: BackfillStatus
}

/** One entry of `GET /api/{symbol}/candles?p=&limit=`, oldest first. */
export interface CandleResponse {
  bucketStart: IsoDateTime
  open: number
  high: number
  low: number
  close: number
  volume: number
}

/** WebSocket ingestion state as reported by the API. */
export type WsStatus = 'Standby' | 'Connecting' | 'Connected' | 'Disconnected'

/** `GET /api/system/stats`, also pushed over the hub as `stats` every 2 s. */
export interface SystemStatsResponse {
  wsStatus: WsStatus
  isLeader: boolean
  ticksPerSecond: number
  ingestLagMs: number
  storedTicksApprox: number
  uptimeSeconds: number
  connectedSince: IsoDateTime | null
  lastReconnect: IsoDateTime | null
  nextRotation: IsoDateTime | null
  activeSymbols: number
  version: string
}

/** `POST /api/admin/symbols` body. */
export interface AddSymbolRequest {
  symbol: string
}

/** RFC 9457 problem details, as returned by every failing endpoint. */
export interface ProblemResponse {
  type: string
  title: string
  status: number
  detail: string | null
  traceId: string | null
}

/** The hub's `price` message: the latest trade for one subscribed symbol. */
export interface LivePrice {
  symbol: string
  price: number
  tradeTime: IsoDateTime
}
