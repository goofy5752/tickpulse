import type { ReactNode } from 'react'
import { InfoTip } from '@/components/InfoTip'
import type { HelpKey } from '@/content/help'
import { cn } from '@/lib/utils'

interface TitleWithTipProps {
  /** The heading element itself; the tip is its sibling so the heading's name stays clean. */
  children: ReactNode
  help: HelpKey
  className?: string
}

/** A heading followed by its ⓘ, aligned on the heading's first line. */
export function TitleWithTip({ children, help, className }: TitleWithTipProps) {
  return (
    <div className={cn('flex min-w-0 items-center gap-1.5', className)}>
      {children}
      <InfoTip topic={help} />
    </div>
  )
}
