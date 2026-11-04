export const GITHUB_URL = 'https://github.com/goofy5752/tickpulse'

/**
 * A file or directory (trailing `/`) in the repository, on `main`. Explicit `blob/` and `tree/`
 * URLs, so a link never depends on GitHub's redirects or the default branch.
 */
export function repoUrl(path: string): string {
  const directory = path.endsWith('/')
  const clean = path.replace(/^\/+|\/+$/g, '')
  return `${GITHUB_URL}/${directory ? 'tree' : 'blob'}/main/${clean}`
}

/**
 * Grafana for the nav and footer links. The image build passes the stack's own Grafana root URL
 * (`GRAFANA_ROOT_URL` in compose); without one, `npm run dev` points at the local observability
 * profile and a build at the public Grafana.
 */
const configuredGrafana = import.meta.env.VITE_GRAFANA_URL?.trim() ?? ''
const defaultGrafana = import.meta.env.DEV ? 'http://localhost:3000' : 'https://grafana.tickpulse.dev'
export const GRAFANA_URL: string = configuredGrafana.length > 0 ? configuredGrafana : defaultGrafana

/** Scalar API reference served by the API. */
export const DOCS_PATH = '/docs'

/** Section ids the page links to. */
export const SECTION_IDS = {
  markets: 'markets',
  chart: 'chart',
  playground: 'playground',
  admin: 'admin',
  system: 'system',
} as const
