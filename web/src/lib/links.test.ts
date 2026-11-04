import { afterEach, describe, expect, it, vi } from 'vitest'

/** links.ts reads the environment once, at import, so every case imports a fresh copy. */
async function load() {
  vi.resetModules()
  return import('./links')
}

afterEach(() => {
  vi.unstubAllEnvs()
})

describe('GRAFANA_URL', () => {
  it('GrafanaUrl_Configured_UsesTheConfiguredUrl', async () => {
    vi.stubEnv('VITE_GRAFANA_URL', ' http://localhost:3091 ')

    const { GRAFANA_URL } = await load()

    expect(GRAFANA_URL).toBe('http://localhost:3091')
  })

  it('GrafanaUrl_NotConfiguredInDevelopment_PointsAtTheLocalStack', async () => {
    vi.stubEnv('VITE_GRAFANA_URL', '')
    vi.stubEnv('DEV', true)

    const { GRAFANA_URL } = await load()

    expect(GRAFANA_URL).toBe('http://localhost:3000')
  })

  it('GrafanaUrl_NotConfiguredInABuild_PointsAtThePublicGrafana', async () => {
    vi.stubEnv('VITE_GRAFANA_URL', '')
    vi.stubEnv('DEV', false)

    const { GRAFANA_URL } = await load()

    expect(GRAFANA_URL).toBe('https://grafana.tickpulse.dev')
  })
})

describe('repoUrl', () => {
  it('RepoUrl_File_LinksTheFileOnMain', async () => {
    const { repoUrl } = await load()

    expect(repoUrl('README.md')).toBe('https://github.com/goofy5752/tickpulse/blob/main/README.md')
  })

  it('RepoUrl_Directory_LinksTheTreeOnMain', async () => {
    const { repoUrl } = await load()

    expect(repoUrl('docs/adr/')).toBe('https://github.com/goofy5752/tickpulse/tree/main/docs/adr')
  })
})
