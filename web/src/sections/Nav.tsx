import { memo, useEffect, useId, useState } from 'react'
import type { SystemStatsResponse } from '@/api/types'
import { InfoTip } from '@/components/InfoTip'
import { PulseMark } from '@/components/PulseMark'
import { StatusPill } from '@/components/StatusPill'
import type { HubStatus } from '@/hooks/useMarketHub'
import { useTheme } from '@/hooks/useTheme'
import { DOCS_PATH, GITHUB_URL, GRAFANA_URL } from '@/lib/links'
import { cn } from '@/lib/utils'

const iconButton =
  'grid size-9 shrink-0 place-items-center rounded-full border border-glass-border bg-glass text-ink-muted transition-colors duration-200 hover:bg-glass-strong hover:text-ink'

function ThemeToggle() {
  const { theme, toggle } = useTheme()
  const next = theme === 'dark' ? 'light' : 'dark'
  return (
    <button type="button" onClick={toggle} aria-label={`Switch to ${next} theme`} className={iconButton}>
      <svg viewBox="0 0 24 24" aria-hidden="true" className="size-4.5" fill="none" stroke="currentColor" strokeWidth="1.8">
        {theme === 'dark' ? (
          <path strokeLinecap="round" strokeLinejoin="round" d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5Z" />
        ) : (
          <>
            <circle cx="12" cy="12" r="4" />
            <path
              strokeLinecap="round"
              d="M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4"
            />
          </>
        )}
      </svg>
    </button>
  )
}

interface NavLink {
  label: string
  href: string
  current?: boolean
  external?: boolean
}

const links: readonly NavLink[] = [
  { label: 'Dashboard', href: '#top', current: true },
  { label: 'API docs', href: DOCS_PATH },
  { label: 'Grafana', href: GRAFANA_URL, external: true },
  { label: 'GitHub', href: GITHUB_URL, external: true },
]

function LinkItem({ link, onNavigate, stacked }: { link: NavLink; onNavigate?: () => void; stacked?: boolean }) {
  return (
    <a
      href={link.href}
      onClick={onNavigate}
      aria-current={link.current ? 'page' : undefined}
      {...(link.external ? { target: '_blank', rel: 'noreferrer' } : {})}
      className={cn(
        'inline-flex items-center gap-1 rounded-full text-[0.8125rem] text-ink-muted transition-colors duration-200 hover:text-ink',
        stacked ? 'w-full rounded-(--radius-control) px-3 py-2.5 text-[0.9375rem]' : 'px-3 py-1.5',
        link.current && 'bg-glass-strong text-ink',
      )}
    >
      {link.label}
      {link.external && (
        <>
          <svg viewBox="0 0 12 12" aria-hidden="true" className="size-2.5 text-ink-faint" fill="none" stroke="currentColor" strokeWidth="1.4">
            <path strokeLinecap="round" strokeLinejoin="round" d="M4 2.5h5.5V8M9.5 2.5 2.5 9.5" />
          </svg>
          <span className="sr-only"> (opens in a new tab)</span>
        </>
      )}
    </a>
  )
}

interface NavProps {
  hubStatus: HubStatus
  stats: SystemStatsResponse | null
}

/**
 * Brand, links, theme and the live status. From 768 px the links sit inline; below that they
 * move into a menu, and below 640 px the status pill shortens too.
 */
export const Nav = memo(function Nav({ hubStatus, stats }: NavProps) {
  const [open, setOpen] = useState(false)
  const menuId = useId()

  useEffect(() => {
    if (!open) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('keydown', onKey)
    }
  }, [open])

  return (
    <header className="glass sticky top-3 z-30">
      <div className="flex h-14 items-center gap-2 pr-2.5 pl-3.5 sm:gap-3 sm:pr-3 sm:pl-4">
        <a href="#top" className="flex shrink-0 items-center gap-2 rounded-md pr-1 text-[0.9375rem] font-semibold tracking-tight">
          <PulseMark className="size-6" />
          TickPulse
        </a>

        <nav aria-label="Primary" className="ml-4 hidden md:block">
          <ul className="flex items-center gap-0.5">
            {links.map((link) => (
              <li key={link.label}>
                <LinkItem link={link} />
              </li>
            ))}
          </ul>
        </nav>

        <div className="ml-auto flex items-center gap-2">
          <StatusPill hubStatus={hubStatus} stats={stats} />
          <span className="flex items-center gap-1.5">
            <ThemeToggle />
            {/* Below 640 px the nav has no room for it; the menu carries it instead. */}
            <span className="hidden sm:contents">
              <InfoTip topic="navTheme" placement="bottom" />
            </span>
          </span>
          <button
            type="button"
            aria-expanded={open}
            aria-controls={menuId}
            aria-label={open ? 'Close menu' : 'Open menu'}
            onClick={() => {
              setOpen((value) => !value)
            }}
            className={cn(iconButton, 'md:hidden')}
          >
            <svg viewBox="0 0 24 24" aria-hidden="true" className="size-4.5" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round">
              {open ? <path d="M6 6l12 12M18 6 6 18" /> : <path d="M4.5 8h15M4.5 16h15" />}
            </svg>
          </button>
        </div>
      </div>

      <nav id={menuId} aria-label="Primary" hidden={!open} className="border-t border-glass-border p-2 md:hidden">
        <ul className="flex flex-col gap-0.5">
          {links.map((link) => (
            <li key={link.label}>
              <LinkItem
                link={link}
                stacked
                onNavigate={() => {
                  setOpen(false)
                }}
              />
            </li>
          ))}
        </ul>
        <p className="mt-1 flex items-center gap-4 border-t border-glass-border px-3 pt-2.5 pb-1 text-[0.75rem] text-ink-faint sm:hidden">
          <span className="flex items-center gap-1.5">
            Live status <InfoTip topic="navStatus" />
          </span>
          <span className="flex items-center gap-1.5">
            Theme <InfoTip topic="navTheme" />
          </span>
        </p>
      </nav>
    </header>
  )
})
