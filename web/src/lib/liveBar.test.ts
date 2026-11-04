import { describe, expect, it } from 'vitest'
import { mergeTrade, reconcileBar, type OhlcBar } from './liveBar'

const MIN = 60
const t0 = Date.UTC(2026, 10, 3, 12, 0) / 1000

function trade(secondsAfterT0: number, price: number) {
  return { price, tradeTime: new Date((t0 + secondsAfterT0) * 1000).toISOString() }
}

const bar: OhlcBar = { time: t0, open: 100, high: 105, low: 98, close: 101 }

describe('mergeTrade', () => {
  it('mergeTrade_SameBucket_ExtendsHighLowAndSetsClose', () => {
    expect(mergeTrade(bar, trade(10, 107), MIN)).toEqual({ time: t0, open: 100, high: 107, low: 98, close: 107 })
    expect(mergeTrade(bar, trade(10, 95), MIN)).toEqual({ time: t0, open: 100, high: 105, low: 95, close: 95 })
  })

  it('mergeTrade_Accumulates_HighAndLowSurviveLaterTrades', () => {
    let current: OhlcBar | undefined = bar
    for (const [at, price] of [
      [5, 110],
      [15, 90],
      [25, 102],
    ] as const) {
      current = mergeTrade(current, trade(at, price), MIN)
    }

    expect(current).toEqual({ time: t0, open: 100, high: 110, low: 90, close: 102 })
  })

  it('mergeTrade_NewBucket_StartsFromTheTrade', () => {
    expect(mergeTrade(bar, trade(130, 99.5), MIN)).toEqual({
      time: t0 + 2 * MIN,
      open: 99.5,
      high: 99.5,
      low: 99.5,
      close: 99.5,
    })
  })

  it('mergeTrade_OlderOrInvalidTrade_LeavesBarAlone', () => {
    expect(mergeTrade(bar, trade(-5, 1), MIN)).toBe(bar)
    expect(mergeTrade(bar, { price: 1, tradeTime: 'not a time' }, MIN)).toBe(bar)
    expect(mergeTrade(undefined, trade(5, 1), MIN)).toBeUndefined()
  })
})

describe('reconcileBar', () => {
  const server: OhlcBar = { time: t0, open: 100, high: 104, low: 99, close: 103 }

  it('reconcileBar_SameBucket_KeepsServerCloseButWidestRange', () => {
    const local: OhlcBar = { time: t0, open: 100, high: 110, low: 97, close: 108 }

    expect(reconcileBar(local, server)).toEqual({ time: t0, open: 100, high: 110, low: 97, close: 103 })
  })

  it('reconcileBar_LocalOpenedANewerBucket_KeepsLocal', () => {
    const local: OhlcBar = { time: t0 + MIN, open: 103, high: 103, low: 103, close: 103 }

    expect(reconcileBar(local, server)).toBe(local)
  })

  it('reconcileBar_ServerIsNewerOrNoLocal_TakesServer', () => {
    expect(reconcileBar({ ...server, time: t0 - MIN }, server)).toBe(server)
    expect(reconcileBar(undefined, server)).toBe(server)
    expect(reconcileBar(server, undefined)).toBeUndefined()
  })
})
