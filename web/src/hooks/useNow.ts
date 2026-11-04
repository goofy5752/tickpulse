import { useSyncExternalStore } from 'react'

let now = Date.now()

function subscribe(intervalMs: number) {
  return (onChange: () => void) => {
    now = Date.now()
    const id = setInterval(() => {
      now = Date.now()
      onChange()
    }, intervalMs)
    return () => {
      clearInterval(id)
    }
  }
}

const subscribers = new Map<number, (onChange: () => void) => () => void>()

function subscriberFor(intervalMs: number) {
  let subscriber = subscribers.get(intervalMs)
  if (!subscriber) {
    subscriber = subscribe(intervalMs)
    subscribers.set(intervalMs, subscriber)
  }
  return subscriber
}

/** The current time in ms, refreshed every `intervalMs`, for "3h ago" style labels. */
export function useNow(intervalMs = 10_000): number {
  return useSyncExternalStore(
    subscriberFor(intervalMs),
    () => now,
    () => now,
  )
}
