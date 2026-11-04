import { useCallback, useId, useRef, useState, type KeyboardEvent } from 'react'
import { createPortal } from 'react-dom'
import { revealOption, useAnchoredPopover } from '@/hooks/useAnchoredPopover'
import { control } from '@/lib/styles'
import { cn } from '@/lib/utils'

export interface SelectOption<T extends string> {
  value: T
  label: string
}

interface SelectProps<T extends string> {
  /** The trigger's id, so a `<label htmlFor>` names it. */
  id: string
  value: T
  options: readonly SelectOption<T>[]
  onChange: (value: T) => void
  /** Monospace text, for code-like values. */
  mono?: boolean
  className?: string
}

/**
 * A select in the dashboard's glass style: a button that opens a listbox below it. Keyboard as a
 * native select: Arrow keys, Home and End move, Enter or Space picks, Escape and Tab close, and
 * Arrow Up or Down on the closed button opens it on the current value.
 */
export function Select<T extends string>({ id, value, options, onChange, mono = false, className }: SelectProps<T>) {
  const listId = useId()
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const listRef = useRef<HTMLUListElement>(null)
  const selectedIndex = Math.max(
    0,
    options.findIndex((option) => option.value === value),
  )
  const current = options[selectedIndex]

  const close = useCallback((reason: 'escape' | 'outside' | 'pick') => {
    setOpen(false)
    if (reason !== 'outside') triggerRef.current?.focus()
  }, [])
  useAnchoredPopover(open, triggerRef, listRef, { matchWidth: true, onClose: close })

  const show = (index: number) => {
    setActive(index)
    setOpen(true)
    requestAnimationFrame(() => {
      listRef.current?.focus()
      revealOption(listRef.current, index)
    })
  }
  const pick = (index: number) => {
    const option = options[index]
    if (option && option.value !== value) onChange(option.value)
    close('pick')
  }
  const move = (index: number) => {
    const next = Math.min(Math.max(index, 0), options.length - 1)
    setActive(next)
    revealOption(listRef.current, next)
  }

  const onTriggerKey = (event: KeyboardEvent) => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      show(selectedIndex)
    }
  }
  const onListKey = (event: KeyboardEvent) => {
    const keys: Record<string, () => void> = {
      ArrowDown: () => {
        move(active + 1)
      },
      ArrowUp: () => {
        move(active - 1)
      },
      Home: () => {
        move(0)
      },
      End: () => {
        move(options.length - 1)
      },
      Enter: () => {
        pick(active)
      },
      ' ': () => {
        pick(active)
      },
    }
    if (event.key === 'Tab') {
      setOpen(false)
      return
    }
    const action = keys[event.key]
    if (!action) return
    event.preventDefault()
    action()
  }

  return (
    <>
      <button
        ref={triggerRef}
        id={id}
        type="button"
        role="combobox"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        onClick={() => {
          if (open) setOpen(false)
          else show(selectedIndex)
        }}
        onKeyDown={onTriggerKey}
        className={cn(control, 'flex cursor-pointer items-center justify-between gap-2 text-left', mono && 'font-mono text-[0.8125rem]', className)}
      >
        <span className="truncate">{current?.label}</span>
        <Chevron open={open} />
      </button>
      {open &&
        createPortal(
          <ul
            ref={listRef}
            id={listId}
            role="listbox"
            tabIndex={-1}
            aria-labelledby={id}
            aria-activedescendant={`${listId}-${String(active)}`}
            onKeyDown={onListKey}
            onBlur={(event) => {
              if (!(event.relatedTarget instanceof Node && listRef.current?.contains(event.relatedTarget))) setOpen(false)
            }}
            className="popover p-1 outline-none"
          >
            {options.map((option, index) => (
              <li
                key={option.value}
                id={`${listId}-${String(index)}`}
                role="option"
                aria-selected={option.value === value}
                data-index={index}
                data-active={index === active ? '' : undefined}
                onPointerMove={() => {
                  if (index !== active) setActive(index)
                }}
                onClick={() => {
                  pick(index)
                }}
                className={cn('popover-option', mono && 'font-mono')}
              >
                <span>{option.label}</span>
                {option.value === value && <Check />}
              </li>
            ))}
          </ul>,
          document.body,
        )}
    </>
  )
}

export function Chevron({ open = false }: { open?: boolean }) {
  return (
    <svg
      viewBox="0 0 12 12"
      aria-hidden="true"
      className={cn('size-3 shrink-0 text-ink-faint transition-transform duration-200', open && 'rotate-180')}
      fill="none"
    >
      <path d="M3 4.5 6 7.5 9 4.5" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

function Check() {
  return (
    <svg viewBox="0 0 12 12" aria-hidden="true" className="size-3 shrink-0 text-sma" fill="none">
      <path d="m2.5 6.25 2.25 2.25L9.5 3.75" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}
