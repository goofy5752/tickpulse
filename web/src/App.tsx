import { useTheme } from '@/hooks/useTheme'
import { cn } from '@/lib/utils'

const GITHUB_URL = 'https://github.com/goofy5752/tickpulse'

function PulseMark({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 32 32" aria-hidden="true" className={className}>
      <defs>
        <linearGradient id="pulse-mark" x1="0" x2="1" y1="0" y2="0">
          <stop offset="0" stopColor="var(--color-up)" />
          <stop offset="1" stopColor="var(--color-sma)" />
        </linearGradient>
      </defs>
      <polyline
        points="3,18 9,18 12.5,8 17,26 20.5,13 23,18 29,18"
        fill="none"
        stroke="url(#pulse-mark)"
        strokeWidth="2.6"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  )
}

function ThemeToggle() {
  const { theme, toggle } = useTheme()
  const next = theme === 'dark' ? 'light' : 'dark'
  return (
    <button
      type="button"
      onClick={toggle}
      aria-label={`Switch to ${next} theme`}
      title={`Switch to ${next} theme`}
      className="grid size-9 place-items-center rounded-full border border-glass-border bg-glass text-ink-muted transition-colors duration-200 hover:bg-glass-strong hover:text-ink"
    >
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

const navLinks = [
  { label: 'Dashboard', href: '#top', current: true },
  { label: 'API docs', href: '/docs' },
  { label: 'GitHub', href: GITHUB_URL, external: true },
] as const

function Nav() {
  return (
    <header className="glass flex items-center justify-between gap-3 px-3 py-2.5 sm:px-4">
      <a href="#top" className="flex items-center gap-2 rounded-md pr-1 text-[0.9375rem] font-semibold tracking-tight">
        <PulseMark className="size-6" />
        TickPulse
      </a>
      <nav aria-label="Primary" className="flex items-center gap-1 sm:gap-1.5">
        <ul className="hidden items-center gap-1 sm:flex">
          {navLinks.map((link) => (
            <li key={link.label}>
              <a
                href={link.href}
                aria-current={'current' in link ? 'page' : undefined}
                {...('external' in link ? { target: '_blank', rel: 'noreferrer' } : {})}
                className={cn(
                  'rounded-full px-3 py-1.5 text-[0.8125rem] text-ink-muted transition-colors duration-200 hover:text-ink',
                  'current' in link && 'bg-glass-strong text-ink',
                )}
              >
                {link.label}
                {'external' in link && (
                  <span aria-hidden="true" className="ml-1 text-ink-faint">
                    ↗
                  </span>
                )}
              </a>
            </li>
          ))}
        </ul>
        <ThemeToggle />
      </nav>
    </header>
  )
}

const roles = [
  { name: 'Up', token: '--color-up', className: 'bg-up', note: 'Rising prices, positive change' },
  { name: 'Down', token: '--color-down', className: 'bg-down', note: 'Falling prices, negative change' },
  { name: 'SMA', token: '--color-sma', className: 'bg-sma', note: 'Moving average and its window' },
] as const

function ThemePreview() {
  return (
    <section aria-labelledby="theme-preview-title" className="glass p-5 sm:p-7">
      <h2 id="theme-preview-title" className="text-base font-semibold tracking-tight">
        Colour roles
      </h2>
      <p className="mt-1 max-w-[60ch] text-ink-muted">
        Colour carries meaning on every chart and figure. Switch the theme to check both palettes.
      </p>

      <ul className="mt-6 grid gap-3 sm:grid-cols-3">
        {roles.map((role) => (
          <li key={role.name} className="flex items-center gap-3 rounded-(--radius-control) bg-glass-strong p-3">
            <span aria-hidden="true" className={cn('size-9 shrink-0 rounded-full', role.className)} />
            <span className="min-w-0">
              <span className="block font-medium">{role.name}</span>
              <span className="block truncate text-[0.8125rem] text-ink-muted">{role.note}</span>
            </span>
          </li>
        ))}
      </ul>

      <div className="mt-6 flex flex-wrap items-baseline gap-x-8 gap-y-3" aria-label="Type specimen">
        <span className="num text-[2rem] leading-none font-semibold tracking-tight">0123456789</span>
        <span className="num rounded-full bg-up-soft px-2.5 py-1 text-[0.8125rem] font-medium text-up">+ 1111</span>
        <span className="num rounded-full bg-down-soft px-2.5 py-1 text-[0.8125rem] font-medium text-down">− 1111</span>
        <code className="text-[0.8125rem] text-ink-muted">GET /api/BTCUSDT/24hAvgPrice</code>
      </div>
    </section>
  )
}

export function App() {
  return (
    <div id="top" className="mx-auto flex min-h-dvh w-full max-w-[1280px] flex-col gap-6 px-4 py-4 sm:px-6 sm:py-6 lg:px-8">
      <Nav />
      <main id="main" className="flex flex-col gap-6">
        <h1 className="sr-only">TickPulse market dashboard</h1>
        <ThemePreview />
      </main>
    </div>
  )
}
