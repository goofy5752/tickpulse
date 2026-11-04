export const GITHUB_URL = 'https://github.com/goofy5752/tickpulse'

/** The public Grafana; a deployment can point the dashboard at its own with VITE_GRAFANA_URL. */
const configuredGrafana = import.meta.env.VITE_GRAFANA_URL?.trim() ?? ''
export const GRAFANA_URL: string = configuredGrafana.length > 0 ? configuredGrafana : 'https://grafana.tickpulse.dev'

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
