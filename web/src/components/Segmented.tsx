import { useId, type ReactNode } from 'react'
import { cn } from '@/lib/utils'

export interface SegmentedOption<T extends string> {
  value: T
  label: ReactNode
}

interface SegmentedProps<T extends string> {
  /** Names the group for assistive tech. */
  legend: string
  /** A short visible label drawn before the segments from 640 px (the legend stays the accessible name). */
  visibleLabel?: string
  options: readonly SegmentedOption<T>[]
  value: T
  onChange: (value: T) => void
  className?: string
  /** Mono for values the API sees verbatim (media types, periods). */
  mono?: boolean
}

/**
 * A single choice drawn as segments. Native radios underneath, so arrow keys, form semantics and
 * screen readers work as they do for any radio group.
 */
export function Segmented<T extends string>({
  legend,
  visibleLabel,
  options,
  value,
  onChange,
  className,
  mono = false,
}: SegmentedProps<T>) {
  const name = useId()
  return (
    <fieldset className={cn('flex min-w-0 items-center gap-2', className)}>
      <legend className="sr-only">{legend}</legend>
      {visibleLabel && (
        <span aria-hidden="true" className="hidden shrink-0 text-[0.75rem] font-medium text-ink-muted sm:inline">
          {visibleLabel}
        </span>
      )}
      <div className="flex min-w-0 flex-wrap rounded-[1.125rem] border border-glass-border bg-glass p-0.5">
        {options.map((option) => (
          <label key={option.value} className="relative min-w-0 grow">
            <input
              type="radio"
              name={name}
              value={option.value}
              checked={value === option.value}
              onChange={() => {
                onChange(option.value)
              }}
              className="peer sr-only"
            />
            <span
              className={cn(
                'block cursor-pointer rounded-full px-3 py-1 text-center text-[0.8125rem] whitespace-nowrap text-ink-muted transition-colors duration-150 peer-checked:bg-glass-strong peer-checked:font-medium peer-checked:text-ink peer-checked:shadow-[inset_0_0_0_1px_var(--color-glass-border)] peer-focus-visible:outline-2 peer-focus-visible:outline-offset-1 peer-focus-visible:outline-sma hover:text-ink',
                mono && 'font-mono text-[0.75rem]',
              )}
            >
              {option.label}
            </span>
          </label>
        ))}
      </div>
    </fieldset>
  )
}
