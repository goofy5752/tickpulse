/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Optional absolute API origin; empty means same-origin (the default). */
  readonly VITE_API_BASE_URL?: string
  /** Grafana for the nav and footer links; the image build passes the stack's GRAFANA_ROOT_URL. */
  readonly VITE_GRAFANA_URL?: string
  /** "1" serves simulated market data in `npm run dev` (never in a build). */
  readonly VITE_MOCK?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
