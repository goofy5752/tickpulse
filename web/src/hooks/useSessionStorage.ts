import { useCallback, useState } from 'react'

function read(key: string): string {
  try {
    return window.sessionStorage.getItem(key) ?? ''
  } catch {
    // Storage can be blocked (privacy settings, sandboxed frames); the value then lives in memory.
    return ''
  }
}

function write(key: string, value: string) {
  try {
    if (value.length === 0) window.sessionStorage.removeItem(key)
    else window.sessionStorage.setItem(key, value)
  } catch {
    // See read(): keep working without persistence.
  }
}

/**
 * A string kept in this tab's sessionStorage: it survives a reload, is gone when the tab closes,
 * and is never shared with other tabs. An empty string removes the entry.
 */
export function useSessionStorage(key: string): [string, (value: string) => void] {
  const [value, setValue] = useState(() => read(key))
  const update = useCallback(
    (next: string) => {
      setValue(next)
      write(key, next)
    },
    [key],
  )
  return [value, update]
}
