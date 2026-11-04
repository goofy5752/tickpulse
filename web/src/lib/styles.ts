/** Class strings shared by the dashboard's forms, so every field looks and behaves the same. */

export const fieldLabel = 'mb-1.5 block text-[0.75rem] font-medium text-ink-muted'

export const control =
  'num h-9 w-full rounded-(--radius-control) border border-glass-border bg-glass-strong px-3 text-[0.875rem] text-ink transition-colors duration-150 hover:border-ink-faint/40'

export const selectControl = `${control} select-chevron cursor-pointer appearance-none pr-8`

/** The one filled button per panel. */
export const primaryButton =
  'inline-flex h-9 shrink-0 items-center justify-center gap-2 rounded-(--radius-control) bg-ink px-4 text-[0.8125rem] font-semibold text-bg transition-[opacity,transform] duration-150 hover:opacity-90 active:translate-y-px disabled:cursor-not-allowed disabled:opacity-45'

export const quietButton =
  'inline-flex h-9 shrink-0 items-center justify-center gap-1.5 rounded-(--radius-control) border border-glass-border px-3 text-[0.8125rem] text-ink-muted transition-colors duration-150 hover:bg-glass-strong hover:text-ink disabled:cursor-not-allowed disabled:opacity-45'

/** A panel's visible heading and its one-line description. */
export const sectionTitle = 'text-[0.9375rem] font-semibold tracking-tight text-ink'
export const sectionLead = 'mt-0.5 max-w-[60ch] text-[0.8125rem] text-ink-muted'
