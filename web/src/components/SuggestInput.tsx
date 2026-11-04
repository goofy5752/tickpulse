import { useCallback, useId, useRef, useState, type InputHTMLAttributes, type KeyboardEvent } from 'react'
import { createPortal } from 'react-dom'
import { revealOption, useAnchoredPopover } from '@/hooks/useAnchoredPopover'
import { cn } from '@/lib/utils'

interface SuggestInputProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'list'> {
  id: string
  value: string
  onChange: (value: string) => void
  /** Offered below the field, filtered by what is typed. */
  suggestions: readonly string[]
}

/**
 * A text field with a suggestion list in the dashboard's glass style, in place of a native
 * `<datalist>`. Typing filters the list; Arrow Down opens it and moves, Enter picks, Escape closes.
 * Anything may still be typed: the list only suggests.
 */
export function SuggestInput({ id, value, onChange, suggestions, className, onKeyDown, ...rest }: SuggestInputProps) {
  const listId = useId()
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(-1)
  const inputRef = useRef<HTMLInputElement>(null)
  const listRef = useRef<HTMLUListElement>(null)

  const query = value.trim().toUpperCase()
  const matches = suggestions.filter((item) => item !== query && item.includes(query))
  const shown = open && matches.length > 0

  const close = useCallback(() => {
    setOpen(false)
    setActive(-1)
  }, [])
  useAnchoredPopover(shown, inputRef, listRef, { matchWidth: true, onClose: close })

  const pick = (item: string) => {
    onChange(item)
    close()
  }

  const onKey = (event: KeyboardEvent<HTMLInputElement>) => {
    onKeyDown?.(event)
    if (event.defaultPrevented) return
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      if (!open) setOpen(true)
      const step = event.key === 'ArrowDown' ? 1 : -1
      const next = Math.min(Math.max(active + step, 0), matches.length - 1)
      setActive(next)
      revealOption(listRef.current, next)
    } else if (event.key === 'Enter' && shown && active >= 0) {
      const item = matches[active]
      if (item) {
        event.preventDefault()
        pick(item)
      }
    } else if (event.key === 'Tab') {
      close()
    }
  }

  return (
    <>
      <input
        {...rest}
        ref={inputRef}
        id={id}
        role="combobox"
        aria-autocomplete="list"
        aria-expanded={shown}
        aria-controls={shown ? listId : undefined}
        aria-activedescendant={shown && active >= 0 ? `${listId}-${String(active)}` : undefined}
        value={value}
        onChange={(event) => {
          onChange(event.target.value)
          setOpen(true)
          setActive(-1)
        }}
        onFocus={() => {
          setOpen(true)
        }}
        onBlur={(event) => {
          if (!(event.relatedTarget instanceof Node && listRef.current?.contains(event.relatedTarget))) close()
        }}
        onKeyDown={onKey}
        className={className}
      />
      {shown &&
        createPortal(
          <ul ref={listRef} id={listId} role="listbox" aria-labelledby={id} className="popover p-1">
            {matches.map((item, index) => (
              <li
                key={item}
                id={`${listId}-${String(index)}`}
                role="option"
                aria-selected={index === active}
                data-index={index}
                data-active={index === active ? '' : undefined}
                onPointerDown={(event) => {
                  // Keep focus in the field, so the blur does not close the list before the click.
                  event.preventDefault()
                }}
                onPointerMove={() => {
                  if (index !== active) setActive(index)
                }}
                onClick={() => {
                  pick(item)
                }}
                className={cn('popover-option font-mono')}
              >
                {item}
              </li>
            ))}
          </ul>,
          document.body,
        )}
    </>
  )
}
