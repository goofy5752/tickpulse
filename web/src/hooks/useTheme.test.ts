import { act, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { THEME_COLORS, THEME_STORAGE_KEY, useTheme } from './useTheme'

function themeColor(): string | null {
  return document.querySelector('meta[name="theme-color"]')?.getAttribute('content') ?? null
}

beforeEach(() => {
  document.documentElement.dataset.theme = 'dark'
  document.head.replaceChildren()
  const meta = document.createElement('meta')
  meta.name = 'theme-color'
  meta.content = '#07070d'
  document.head.append(meta)
  localStorage.clear()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('useTheme', () => {
  it('useTheme_Toggle_UpdatesAttributeThemeColorAndStorage', () => {
    const { result } = renderHook(() => useTheme())
    expect(result.current.theme).toBe('dark')

    act(() => {
      result.current.toggle()
    })

    expect(result.current.theme).toBe('light')
    expect(document.documentElement.dataset.theme).toBe('light')
    expect(themeColor()).toBe(THEME_COLORS.light)
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light')

    act(() => {
      result.current.toggle()
    })

    expect(themeColor()).toBe(THEME_COLORS.dark)
  })

  it('useTheme_StorageUnavailable_StillSwitchesTheme', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('blocked', 'SecurityError')
    })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('blocked', 'SecurityError')
    })

    const { result } = renderHook(() => useTheme())
    act(() => {
      result.current.toggle()
    })

    expect(result.current.theme).toBe('light')
    expect(document.documentElement.dataset.theme).toBe('light')
  })
})
