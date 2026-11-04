import type { ReactNode } from 'react'
import { InfoTip } from '@/components/InfoTip'
import type { HelpKey } from '@/content/help'
import { cn } from '@/lib/utils'

interface FieldLabelProps {
  htmlFor?: string
  children: ReactNode
  /** The tooltip next to the label. It sits beside the <label>, so it never joins the field's name. */
  help?: HelpKey
  className?: string
}

/** A form label with its ⓘ, at the height the label alone had, so adding the tip shifts nothing. */
export function FieldLabel({ htmlFor, children, help, className }: FieldLabelProps) {
  const text = 'text-[0.75rem] font-medium text-ink-muted'
  return (
    <div className={cn('mb-1.5 flex min-w-0 items-center gap-1.5', className)}>
      {htmlFor ? (
        <label htmlFor={htmlFor} className={text}>
          {children}
        </label>
      ) : (
        <span className={text}>{children}</span>
      )}
      {help && <InfoTip topic={help} />}
    </div>
  )
}
