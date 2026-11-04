import { useCallback, useEffect, useState } from 'react'

export type Theme = 'dark' | 'light'

/** Shared with the pre-paint script public/theme-init.js, which applies the stored theme before React loads. */
export const THEME_STORAGE_KEY = 'tickpulse.theme'

/** Browser chrome colour per theme; matches `--color-bg` in theme.css and public/theme-init.js. */
export const THEME_COLORS: Record<Theme, string> = { dark: '#07070d', light: '#f3f2fa' }

function initialTheme(): Theme {
  if (document.documentElement.dataset.theme === 'light') return 'light'
  try {
    return localStorage.getItem(THEME_STORAGE_KEY) === 'light' ? 'light' : 'dark'
  } catch {
    return 'dark'
  }
}

/** Dark by default; the choice is remembered per browser. */
export function useTheme() {
  const [theme, setTheme] = useState<Theme>(initialTheme)

  useEffect(() => {
    document.documentElement.dataset.theme = theme
    document.querySelector('meta[name="theme-color"]')?.setAttribute('content', THEME_COLORS[theme])
    try {
      localStorage.setItem(THEME_STORAGE_KEY, theme)
    } catch {
      // Storage can be unavailable (private mode, blocked site data); the theme still applies.
    }
  }, [theme])

  const toggle = useCallback(() => {
    setTheme((current) => (current === 'dark' ? 'light' : 'dark'))
  }, [])

  return { theme, toggle }
}
