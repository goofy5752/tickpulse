import { afterEach, describe, expect, it } from 'vitest'
import indexHtml from '../../index.html?raw'
import themeInit from '../../public/theme-init.js?raw'
import { THEME_COLORS, THEME_STORAGE_KEY } from './useTheme'

// The API serves the dashboard with `script-src 'self'`: no inline script runs, so index.html may only load
// scripts from files, and the pre-paint theme script lives in public/theme-init.js.
describe('index.html under the Content-Security-Policy', () => {
  it('has no inline script and no inline event handler', () => {
    const doc = new DOMParser().parseFromString(indexHtml, 'text/html')
    const scripts = [...doc.querySelectorAll('script')]

    expect(scripts.length).toBeGreaterThan(0)
    for (const script of scripts) {
      expect(script.getAttribute('src'), script.outerHTML).toBeTruthy()
      expect(script.textContent.trim(), script.outerHTML).toBe('')
    }
    for (const element of doc.querySelectorAll('*')) {
      expect(element.getAttributeNames().filter((name) => name.startsWith('on')), element.tagName).toEqual([])
    }
  })

  it('loads the theme script in <head>, before the app, as a classic blocking script', () => {
    const doc = new DOMParser().parseFromString(indexHtml, 'text/html')
    const theme = doc.head.querySelector('script[src="/theme-init.js"]')

    expect(theme).not.toBeNull()
    expect(theme?.hasAttribute('type')).toBe(false)
    expect(theme?.hasAttribute('async')).toBe(false)
    expect(theme?.hasAttribute('defer')).toBe(false)
  })
})

describe('theme-init.js', () => {
  // The script is plain browser JavaScript from public/; run it as the browser would.
  // eslint-disable-next-line @typescript-eslint/no-implied-eval
  const run = new Function(themeInit) as () => void

  afterEach(() => {
    localStorage.clear()
    document.documentElement.removeAttribute('data-theme')
    document.head.innerHTML = ''
  })

  function setUp(stored: string | null) {
    document.documentElement.dataset.theme = 'dark'
    document.head.innerHTML = `<meta name="theme-color" content="${THEME_COLORS.dark}">`
    if (stored !== null) localStorage.setItem(THEME_STORAGE_KEY, stored)
  }

  it('applies a remembered light theme and its browser colour', () => {
    setUp('light')

    run()

    expect(document.documentElement.dataset.theme).toBe('light')
    expect(document.querySelector('meta[name="theme-color"]')?.getAttribute('content')).toBe(THEME_COLORS.light)
  })

  it.each([null, 'dark', 'other'])('keeps the dark default for %s', (stored) => {
    setUp(stored)

    run()

    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(document.querySelector('meta[name="theme-color"]')?.getAttribute('content')).toBe(THEME_COLORS.dark)
  })
})
