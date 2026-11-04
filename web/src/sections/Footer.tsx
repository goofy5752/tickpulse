import { PulseMark } from '@/components/PulseMark'
import { DOCS_PATH, GITHUB_URL, GRAFANA_URL, repoUrl } from '@/lib/links'

interface FooterLink {
  label: string
  href: string
  external: boolean
}

const groups: readonly { title: string; links: readonly FooterLink[] }[] = [
  {
    title: 'Read',
    links: [
      { label: 'README', href: repoUrl('README.md'), external: true },
      { label: 'Engineering notes', href: repoUrl('engineering-notes.md'), external: true },
      { label: 'Architecture decisions', href: repoUrl('docs/adr/'), external: true },
      { label: 'Deployment', href: repoUrl('docs/deployment.md'), external: true },
    ],
  },
  {
    title: 'Use',
    links: [
      { label: 'API reference', href: DOCS_PATH, external: false },
      { label: 'Grafana', href: GRAFANA_URL, external: true },
      { label: 'Source on GitHub', href: GITHUB_URL, external: true },
    ],
  },
]

const STACK = ['.NET 10', 'PostgreSQL 17 with TimescaleDB', 'SignalR', 'React 19', 'TanStack Query', 'lightweight-charts', 'OpenTelemetry']

/** Product name, what it is built with, and where to read more. */
export function Footer() {
  return (
    <footer className="mt-auto border-t border-glass-border pt-8 pb-10">
      <div className="grid grid-cols-1 gap-8 sm:grid-cols-[minmax(0,1fr)_auto]">
        <div className="min-w-0">
          <p className="flex items-center gap-2 text-[0.9375rem] font-semibold tracking-tight">
            <PulseMark className="size-5" />
            TickPulse
          </p>
          <p className="mt-2 max-w-[52ch] text-[0.8125rem] text-ink-muted">
            Binance spot trades collected as they happen, with 24h averages and moving averages served over a JSON and XML API.
          </p>
          <ul className="mt-4 flex flex-wrap gap-1.5" aria-label="Built with">
            {STACK.map((item) => (
              <li key={item} className="rounded-full border border-glass-border px-2.5 py-0.5 text-[0.75rem] text-ink-faint">
                {item}
              </li>
            ))}
          </ul>
        </div>

        <nav aria-label="Project links" className="grid grid-cols-2 gap-x-10 gap-y-6">
          {groups.map((group) => (
            <div key={group.title}>
              <h2 className="text-[0.75rem] font-medium text-ink-faint">{group.title}</h2>
              <ul className="mt-2 space-y-1.5">
                {group.links.map((link) => (
                  <li key={link.label}>
                    <a
                      href={link.href}
                      {...(link.external ? { target: '_blank', rel: 'noreferrer' } : {})}
                      className="rounded text-[0.8125rem] text-ink-muted transition-colors duration-150 hover:text-ink"
                    >
                      {link.label}
                      {link.external && <span className="sr-only"> (opens in a new tab)</span>}
                    </a>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </nav>
      </div>
    </footer>
  )
}
