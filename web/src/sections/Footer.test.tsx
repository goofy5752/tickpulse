import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { DOCS_PATH, GITHUB_URL, GRAFANA_URL } from '@/lib/links'
import { Footer } from './Footer'

afterEach(() => {
  cleanup()
})

function hrefOf(name: RegExp): string | null {
  const nav = screen.getByRole('navigation', { name: 'Project links' })
  return within(nav).getByRole('link', { name }).getAttribute('href')
}

describe('Footer', () => {
  it('Footer_ReadLinks_PointAtRepositoryPathsOnMain', () => {
    render(<Footer />)

    expect(hrefOf(/^README/)).toBe(`${GITHUB_URL}/blob/main/README.md`)
    expect(hrefOf(/^Engineering notes/)).toBe(`${GITHUB_URL}/blob/main/engineering-notes.md`)
    expect(hrefOf(/^Architecture decisions/)).toBe(`${GITHUB_URL}/tree/main/docs/adr`)
    expect(hrefOf(/^Deployment/)).toBe(`${GITHUB_URL}/blob/main/docs/deployment.md`)
  })

  it('Footer_UseLinks_PointAtTheApiDocsGrafanaAndTheRepository', () => {
    render(<Footer />)

    expect(hrefOf(/^API reference/)).toBe(DOCS_PATH)
    expect(hrefOf(/^Grafana/)).toBe(GRAFANA_URL)
    expect(hrefOf(/^Source on GitHub/)).toBe(GITHUB_URL)
  })

  it('Footer_EveryRepositoryLink_TargetsMainExplicitly', () => {
    render(<Footer />)

    const repoLinks = screen
      .getAllByRole('link')
      .map((link) => link.getAttribute('href') ?? '')
      .filter((href) => href.startsWith(`${GITHUB_URL}/`) || href.startsWith(`${GITHUB_URL}#`))

    expect(repoLinks.length).toBeGreaterThan(0)
    for (const href of repoLinks) expect(href).toMatch(/^https:\/\/github\.com\/goofy5752\/tickpulse\/(blob|tree)\/main\/[^#?]+$/)
  })
})
