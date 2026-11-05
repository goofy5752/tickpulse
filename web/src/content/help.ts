/*
 * Every explanatory tooltip on the dashboard, in one place so the copy is easy to review and edit.
 *
 * Each entry is rendered by <InfoTip topic="…" />: a short title, a one-line summary, then optional
 * detail paragraphs, a term list (states, values) and an example shown in mono. The text describes
 * what the API and the dashboard really do; when behaviour changes, change the entry with it.
 */

export interface HelpItem {
  /** A value or state as it appears on screen, e.g. `1d` or "Backfilling". */
  term: string
  text: string
  /** Show the term in mono: it is a literal the API sees. */
  mono?: boolean
}

export interface HelpEntry {
  /** Short heading, also used in the trigger's accessible name ("About …"). */
  title: string
  /** One sentence: what this is. */
  summary: string
  /** How it works and how to use it, one paragraph each. */
  details?: readonly string[]
  items?: readonly HelpItem[]
  /** A concrete case, shown in mono under "Example". */
  example?: string
}

export const HELP = {
  // ---- Nav -------------------------------------------------------------------------------------

  navStatus: {
    title: 'Live status',
    summary: 'Whether TickPulse is receiving Binance trades right now, and how far behind storage is.',
    items: [
      { term: 'Binance live', text: 'The API holds an open WebSocket to Binance and stores trades as they arrive. Phones show "Live".' },
      { term: '… ms lag', text: 'Ingest lag: time from a trade on Binance to its row in the database, for the newest trade of the last stored batch. Turns rose above 5 s.' },
      { term: 'Connecting', text: 'The API is opening its Binance connection, or this page is still connecting to the API.' },
      { term: 'Standby', text: 'The instance answering does not collect trades itself; another instance does, and this one answers API requests.' },
      { term: 'Feed down', text: 'The Binance stream is disconnected. TickPulse reconnects on its own and fetches the trades it missed from Binance.' },
      { term: 'Reconnecting', text: "This page lost its live connection to the API and is retrying; figures are polled over HTTP meanwhile." },
      { term: 'Offline', text: 'This page cannot reach the API at all.' },
    ],
  },


  // ---- Ticker tiles ----------------------------------------------------------------------------

  tilesSection: {
    title: 'Live prices',
    summary: 'One tile per monitored symbol. Select a tile to show that symbol in the chart and the analytics.',
    details: [
      'Prices are pushed over the live connection, up to four times a second per symbol. The violet outline marks the tile on the chart.',
      'Scroll the row sideways when there are more symbols than fit.',
    ],
  },


  tileChange: {
    title: '24h change',
    summary: "How far the latest price is from the close 24 hours ago, in percent.",
    details: [
      'Computed by the API as (latest − close at or before now − 24 h) ÷ that close × 100, rounded to 2 decimals.',
      'The tile reads "no 24h data" when there is no reliable day-ago close: for example while a new symbol\'s backfill is still running, so the nearest stored close may be far older than 24 hours, or when the latest price is itself older than a day.',
    ],
    example: 'close a day ago 76,000, latest 77,520\n(77,520 − 76,000) ÷ 76,000 × 100 = +2.00%',
  },



  // ---- Chart -----------------------------------------------------------------------------------

  chartSection: {
    title: 'Price chart',
    summary: 'Candles or a line of closes for the selected symbol and period, with the SMA and its window.',
    details: [
      'Switch between candles and a line with the icons next to the periods; the line takes the colour of the move across the range shown, and the choice is remembered in this browser.',
      'Pick the symbol with the tiles above and the period with the buttons on the right. Scroll or pinch to zoom, drag to pan; on a phone a vertical swipe still scrolls the page.',
      'Each period opens on a sensible range: 6 h of 1m, 24 h of 5m, 7 days of 30m, a year of 1d and five years of 1w candles, more when the SMA window needs it (up to 1,000 candles), shown as far as history reaches: about six months with backfill.',
    ],
  },

  chartPeriod: {
    title: 'Period',
    summary: 'The width of one candle, or of one point in the line view. It also sets the SMA period p beside the chart.',
    details: [
      'Simple mode (the default) shows quick ranges under the chart, each with a candle size that suits it. Pro mode shows these candle sizes to pick yourself.',
      'Candles show the first, highest, lowest and last price of each bucket; the line joins the last prices (closes). Switch between them with the icons on the left.',
      'Buckets are aligned to UTC, so every client sees the same candles.',
    ],
    items: [
      { term: '1m', mono: true, text: 'One minute, starting on the minute.' },
      { term: '5m', mono: true, text: 'Five minutes, starting at :00, :05, :10 …' },
      { term: '30m', mono: true, text: 'Half an hour, starting at :00 and :30.' },
      { term: '1d', mono: true, text: 'One day, 00:00 to 00:00 UTC.' },
      { term: '1w', mono: true, text: 'One week, Monday 00:00 UTC to the next Monday.' },
    ],
  },


  chartPrice: {
    title: 'Candles and line',
    summary: 'The price per bucket, drawn as candles or as a line of closes. Switch with the icons next to the periods.',
    items: [
      { term: 'Candle', text: 'One per bucket. The body runs from the first to the last trade price; the thin wicks reach the highest and lowest.' },
      { term: 'Teal / rose', text: 'Teal when the bucket closed above its open, rose when it closed below.' },
      { term: 'Line', text: 'Joins the closes, the last price of each bucket, with a soft fill. Teal when the range shown rose overall, rose when it fell.' },
      { term: 'Volume', text: 'The faint bars at the foot: how much of the coin traded in each bucket, coloured like its candle. Big moves on big volume carry more weight.' },
    ],
    details: [
      'The newest bucket is still open: its candle, or the end of the line, moves with live trades. A gap is a bucket without trades. History from before collection began comes from 1-minute klines loaded from Binance.',
    ],
    example: '1d: one candle (or one point) per day, 00:00 to 00:00 UTC',
  },

  chartRange: {
    title: 'Range',
    summary: 'How far back the chart looks. Each range picks a candle size that suits it, and sets the SMA period p too.',
    items: [
      { term: '1D', mono: true, text: 'The last day, in 5-minute candles.' },
      { term: '1W', mono: true, text: 'The last week, in 30-minute candles.' },
      { term: '1M · 3M · 6M', mono: true, text: 'The last one, three or six months, in daily candles.' },
    ],
    details: [
      'Switch to Pro to pick the candle size yourself. Changing the period in the SMA panel clears the range too. Scroll or drag the chart to look further back.',
    ],
  },

  chartSma: {
    title: 'SMA line',
    summary: 'The rolling simple moving average, with the same n and p as the SMA figure.',
    details: [
      'Each point averages the closes of the n buckets that end at that candle. Buckets without trades are skipped, never filled in. The line starts only where a full n-bucket span lies inside the loaded candles.',
      'The open candle is left out. At the window\'s last bucket the line takes the panel\'s SMA value exactly; without a start, that is where the line ends. The "SMA n" label on the price axis marks that value.',
    ],
    example: 'n = 20, p = 1d: each point is the mean close of the 20 days ending that day',
  },

  chartWindow: {
    title: 'Calculation window',
    summary: 'The shaded band marks exactly which buckets the SMA figure averages.',
    details: [
      'It runs from the window start to the window end shown in the panel; the end bucket itself is not included. Dashed lines mark its edges, and an edge without one continues past the loaded candles.',
      'Set a start (s) in the panel to move the band back in time. A note under the chart says when the window lies outside, or begins before, the candles shown.',
    ],
  },


  // ---- Analytics: 24h average -----------------------------------------------------------------

  avgPrice: {
    title: '24h average price',
    summary: 'The arithmetic mean price of every trade TickPulse collected in [now − 24 h, now].',
    details: [
      'Each trade counts once, whatever its size, so this is not a volume-weighted price and differs from Binance\'s weightedAvgPrice. Rounded to 8 decimals by the API; refreshed every 15 s.',
      'Only trades TickPulse stored itself count, including missed trades it fetched again after a reconnect. Backfilled candles never enter this figure. With less than a day of data, the mean covers what exists and Coverage says how much.',
    ],
    example: 'trades at 100, 101 and 105\n(100 + 101 + 105) ÷ 3 = 102',
  },

  avgCoverage: {
    title: 'Coverage',
    summary: 'How much of the 24 h window the collected trades span, from the first to the last trade in it.',
    details: [
      'The bar shows the same share. It measures first to last trade, so a short outage in the middle is not subtracted.',
    ],
    example: '"23h 58m of 24h": collection ran almost the whole day\n"12s of 24h": the stack started 12 seconds ago',
  },

  avgTrades: {
    title: 'Trades averaged',
    summary: 'How many trades went into the average.',
    details: [
      'One trade is one Binance aggregate trade: the fills of one taker order at one price. The more trades, the less a single print moves the average.',
    ],
  },

  // ---- Analytics: SMA ---------------------------------------------------------------------------

  smaSection: {
    title: 'Simple moving average',
    summary: 'The mean of the closes of n complete buckets of period p.',
    details: [
      'A bucket\'s close is its last trade price. Without a start, the window is the last n complete buckets before now; with a start (s), it begins at s and runs forward.',
      'Empty buckets are skipped, not filled in, and the panel says how many points were actually found. Every figure is a live API answer; without s it refreshes every 30 s.',
    ],
  },

  smaN: {
    title: 'Points (n)',
    summary: 'How many buckets the average covers: a whole number from 1 to 1,000.',
    details: ['Type a number, or use the arrow keys to step it by one. The chart\'s SMA line uses the same n.'],
    example: 'n = 20, p = 1d: the closes of the last 20 complete UTC days; today is still open and not included',
  },

  smaP: {
    title: 'Period (p)',
    summary: 'The bucket width. It also switches the chart to the same period.',
    details: ['The same widths as the chart\'s candle period: 1m, 5m, 30m, 1d and 1w, aligned to UTC, with weeks starting on Monday.'],
  },

  smaS: {
    title: 'Start (s), UTC',
    summary: 'Anchors the start of the window. Leave it empty for a window that ends now.',
    details: [
      'The time you pick is read as UTC, not your local time, to the minute, and cannot be in the future.',
      'The window starts at the first bucket that begins at or after s and runs forward n buckets. If fewer than n have closed since then, it stops at the last complete bucket.',
    ],
    example: 'n = 3, p = 1d, s = Oct 1, 09:30\nfirst bucket at or after s: Oct 2, 00:00\naverages the closes of Oct 2, 3 and 4\nwindow end: Oct 5, 00:00 (exclusive)',
  },


  smaValue: {
    title: 'SMA value',
    summary: 'The average of the closes in the window: their sum divided by the points found.',
    details: ['Rounded to 8 decimals by the API. Violet, like the SMA line on the chart, which ends at this value.'],
    example: 'closes 78,354.00, 76,816.00 and 77,453.86\nsum ÷ 3 = 77,541.28666667 (shown as 77,541.29)',
  },

  smaDots: {
    title: 'Window, bucket by bucket',
    summary: 'One mark per bucket in the window, oldest on the left.',
    items: [
      { term: 'Filled', text: 'The bucket has a close and counts.' },
      { term: 'Outlined', text: 'The bucket had no trades and is skipped.' },
      { term: 'Faint', text: 'The bucket has not closed yet, because the start is recent.' },
    ],
    details: ['Above 60 points the marks become one bar filled to the share of points found.'],
  },

  smaPoints: {
    title: 'Points found',
    summary: 'Buckets with a close, out of the n you asked for.',
    details: [
      'Fewer than n when buckets in the window had no trades (an outage, or the symbol was not monitored then), when history does not reach back that far, or when the start is so recent that fewer than n buckets have closed. The note underneath says which.',
      'With backfill, history reaches about six months back, so p = 1w with n = 52 finds about 26 points.',
    ],
    example: '17 / 20 points: 3 of the 20 days had no trades',
  },

  smaWindow: {
    title: 'Window',
    summary: 'The window start and end, in UTC. The end is exclusive: the bucket starting then is not included.',
    details: ['Dates for 1d and 1w, dates with times for the shorter periods. The shaded band on the chart covers the same span.'],
    example: 'Oct 15 → Nov 4 UTC with n = 20, p = 1d\nthe 20 days Oct 15 to Nov 3; Nov 4 is still open',
  },


  // ---- API playground --------------------------------------------------------------------------












  playSection: {
    title: 'API playground',
    summary: 'Sends real requests to the public API and shows each answer exactly as it arrives.',
    details: [
      'Pick an endpoint, fill in its parameters, choose JSON or XML, then send. Values go out as typed, so an invalid one gets the API\'s own validation answer.',
      'The public API allows 300 requests a minute per client by default; beyond that it answers 429.',
    ],
  },
  playEndpoint: {
    title: 'Endpoint',
    summary: 'Which public route the request goes to.',
    items: [
      { term: '24h average', text: '/api/{symbol}/24hAvgPrice: mean of the trades collected in the last 24 h, with coverage.' },
      { term: 'Moving average', text: '/api/{symbol}/SimpleMovingAverage?n=&p=&s=: SMA of n bucket closes.' },
      { term: 'Candles', text: '/api/{symbol}/candles?p=&limit=: OHLCV candles, oldest first, the last one still open.' },
      { term: 'Symbols', text: '/api/symbols: every symbol with its latest price, 24h change and backfill state.' },
    ],
  },
  playSymbol: {
    title: 'Symbol',
    summary: 'The pair to ask about. Suggestions list the monitored symbols.',
    details: ['Case does not matter: the API upper-cases it. A pair that was never monitored gets 404 unknown-symbol.'],
    example: 'btcusdt → /api/BTCUSDT/24hAvgPrice',
  },
  playN: {
    title: 'n',
    summary: 'Number of buckets to average, required, from 1 to 1,000.',
    example: 'n=0 → 400 validation\n"n must be between 1 and 1000."',
  },
  playP: {
    title: 'p',
    summary: 'The bucket width, required: 1w, 1d, 30m, 5m or 1m.',
    details: ['Weeks start Monday 00:00 UTC, days at 00:00 UTC. Any other value gets 400 validation.'],
  },
  playS: {
    title: 's',
    summary: 'Optional window start as an ISO-8601 time; UTC when it has no offset.',
    details: [
      'The window starts at the first bucket at or after s and runs forward n buckets, clipped at the last complete one. Leave it empty for the last n buckets ending now. A time in the future gets 400.',
    ],
    example: '2026-10-01T00:00:00Z\n2026-10-01T02:00:00+02:00 (the same instant)\n2026-10-01 (midnight UTC)',
  },
  playCandlesP: {
    title: 'p',
    summary: 'The candle width: 1w, 1d, 30m, 5m or 1m.',
    details: ['Candles are UTC-aligned; weeks start on Monday.'],
  },
  playLimit: {
    title: 'limit',
    summary: 'How many of the latest candles to return, from 1 to 1,000.',
    details: ['The API returns 300 when the parameter is left out; the playground always sends it.'],
    example: 'p=1d&limit=2 → yesterday\'s candle and today\'s open one',
  },
  playStatus: {
    title: 'Status',
    summary: 'The HTTP status code and its reason. Teal for success, rose for an error.',
    items: [
      { term: '200', mono: true, text: 'The answer is in the body.' },
      { term: '400', mono: true, text: 'A parameter is missing or invalid; the body names it.' },
      { term: '404', mono: true, text: 'unknown-symbol, or no-data when the window holds nothing.' },
      { term: '429', mono: true, text: 'Over the rate limit; Retry-After says when to retry.' },
    ],
  },





  // ---- Admin -----------------------------------------------------------------------------------

  adminSection: {
    title: 'Monitored symbols',
    summary: 'Choose which Binance spot pairs TickPulse collects. Changes need the admin API key.',
    details: ['The list itself is public and matches the tiles at the top. Adding and removing go to the admin API with the X-Api-Key header.'],
  },

  adminKey: {
    title: 'Admin API key',
    summary: 'The key the server runs with, sent as the X-Api-Key header on admin requests.',
    details: [
      'It is kept in this tab\'s sessionStorage: it survives a reload, is gone when you close the tab, and other tabs never see it. It is sent only with add and remove requests, never to the public endpoints. "Forget key" clears it.',
      'A wrong key gets 401. When the server has no key configured, symbol management is switched off and answers 403.',
    ],
  },

  adminSymbol: {
    title: 'Symbol to add',
    summary: 'A Binance spot pair that is currently trading, written as base and quote together.',
    details: [
      '5 to 20 letters or digits, no slash or dash; case does not matter. The server then asks Binance whether the pair is trading: if not, the answer is 422 invalid-symbol, and if Binance cannot be reached, 503.',
    ],
    example: 'SOLUSDT (accepted)\nSOL/USDT (rejected: the slash)',
  },

  adminAdd: {
    title: 'Add symbol',
    summary: 'Starts collecting the pair: TickPulse subscribes to its trades and loads its history.',
    details: [
      'Live trades start within seconds. Where backfill is on, about six months of 1-minute candles are loaded from Binance, roughly 2 minutes per symbol, one symbol at a time. That history feeds the chart and the SMA, never the 24h average.',
      'A symbol that was removed before is reactivated. Where backfill is on, the stretch it missed is filled with 1-minute candles, which feed the chart and the SMA but not the 24h average. Adding one that is already active changes nothing.',
    ],
  },


  adminBackfill: {
    title: 'Backfill status',
    summary: 'Whether the historical candles for each symbol are loaded.',
    items: [
      { term: 'Live only', text: 'No history has been loaded for this symbol yet. This is normal when backfill is off. With backfill on, a symbol from the initial set may show this until its turn comes; it then shows Backfilling.' },
      { term: 'Backfill queued', text: 'Waiting its turn; symbols are loaded one at a time.' },
      { term: 'Backfilling', text: 'Loading 1-minute candles from Binance now.' },
      { term: 'Backfilled', text: 'History is loaded (about six months by default); long-period SMAs have their points.' },
      { term: 'Backfill failed', text: 'History could not be loaded. Live trades still arrive, and it is retried automatically when an instance next takes over ingestion (for example after a restart) or when a Binance ban ends.' },
    ],
  },

  adminRemove: {
    title: 'Remove',
    summary: 'Stops collecting a pair. Its history is kept.',
    details: [
      'You confirm in place first. The subscription stops and the tile goes, but stored trades and candles stay, and the API still answers queries for the pair. Adding it again resumes collection; where backfill is on, the stretch it missed is filled with 1-minute candles, which feed the chart and the SMA but not the 24h average.',
    ],
  },

  // ---- System ----------------------------------------------------------------------------------

  systemSection: {
    title: 'System',
    summary: 'How the ingestion pipeline of the API instance serving this page is doing.',
    items: [
      { term: 'Binance connected', text: 'The trade stream is open. Otherwise connecting, standby or disconnected.' },
      { term: 'Leader, Follower', text: 'Only one instance collects trades at a time; the others just answer API requests. The badge says which kind is serving this page.' },
    ],
    details: [
      'Figures arrive every 2 s over the live connection; while it is down they are polled over HTTP every 10 s, as the line at the bottom says.',
    ],
  },

  systemTicks: {
    title: 'Ticks per second',
    summary: 'Trades stored per second, averaged over roughly the last 10 seconds.',
    details: ['The small line shows the last 30 updates, about one minute (longer while figures are polled). It rises and falls with market activity across all active symbols.'],
  },

  systemLag: {
    title: 'Ingest lag',
    summary: 'Time from a trade on Binance to its row in the database.',
    details: [
      'Measured for the newest trade of the last stored batch. Trades are written in batches every 500 ms or 5,000 trades, whichever comes first, so a few hundred milliseconds is normal. It turns rose above 5 s.',
    ],
  },



  systemConnection: {
    title: 'Binance connection',
    summary: 'The WebSocket TickPulse holds open to Binance, and when it is renewed.',
    details: [
      'Binance closes every WebSocket connection after 24 hours. TickPulse replaces its own after 23 h: the new connection opens and subscribes before the old one closes, so no trades are lost at the handover.',
      'The bar shows how much of the current connection\'s lifetime has passed.',
    ],
  },


  systemNextRotation: {
    title: 'Next rotation',
    summary: 'When the current connection is due to be replaced by a fresh one.',
    details: ['23 h after it opened, an hour before Binance would cut it. Hover for the UTC time.'],
  },

  // ---- Footer ----------------------------------------------------------------------------------


} as const satisfies Record<string, HelpEntry>

export type HelpKey = keyof typeof HELP

export const HELP_KEYS = Object.keys(HELP) as HelpKey[]
