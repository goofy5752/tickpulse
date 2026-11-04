/*
 * Pretty-printing and light syntax highlighting for the API playground's raw responses.
 *
 * Both formatters work on the text as it arrived, token by token, and never parse it into values:
 * a JSON number such as 1.50000000 keeps every digit, and XML keeps its namespaces, `i:nil`
 * markers and entities. Anything that does not tokenise falls back to the raw text, so the
 * playground never shows something the API did not send.
 */

/** name = JSON key or XML tag, string = quoted value, value = number/literal/text, attr = XML attribute name. */
export type TokenKind = 'punct' | 'name' | 'string' | 'value' | 'attr' | 'meta' | 'plain'

export interface Token {
  kind: TokenKind
  text: string
}

export interface CodeLine {
  /** Nesting depth; the renderer turns it into two spaces per level. */
  indent: number
  tokens: Token[]
}

/** Above this size the body is shown as plain text: highlighting would only cost frames. */
const MAX_HIGHLIGHT_CHARS = 250_000
/** Deeper nesting than this is shown as plain text. No API answer comes close. */
const MAX_INDENT = 64

class LineBuilder {
  readonly lines: CodeLine[] = []
  private current: CodeLine = { indent: 0, tokens: [] }

  push(kind: TokenKind, text: string) {
    this.current.tokens.push({ kind, text })
  }

  /** Ends the current line (if it has anything on it) and starts one at `indent`. */
  break(indent: number) {
    if (this.current.tokens.length > 0) this.lines.push(this.current)
    this.current = { indent, tokens: [] }
  }

  done(): CodeLine[] {
    this.break(0)
    return this.lines
  }
}

const JSON_TOKEN = /\s+|"(?:[^"\\\n]|\\.)*"|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null|[{}[\]:,]/y

function tokeniseJson(raw: string): string[] | null {
  const tokens: string[] = []
  JSON_TOKEN.lastIndex = 0
  while (JSON_TOKEN.lastIndex < raw.length) {
    const start = JSON_TOKEN.lastIndex
    const match = JSON_TOKEN.exec(raw)
    if (!match || JSON_TOKEN.lastIndex === start) return null
    if (!/^\s/.test(match[0])) tokens.push(match[0])
  }
  return tokens
}

type JsonState = 'key-or-close' | 'key' | 'colon' | 'value' | 'value-or-close' | 'comma-or-close'

/**
 * Checks the token sequence against the JSON grammar with an explicit stack of open brackets
 * (no recursion, so deep nesting cannot overflow the call stack). Mismatched brackets, a missing
 * colon or comma, a non-string key or trailing tokens all fail.
 */
function isWellFormedJson(tokens: readonly string[]): boolean {
  const stack: { open: '{' | '['; state: JsonState }[] = []
  // An object, not a let: the helpers below set it, and flow analysis cannot see into closures.
  const root = { done: false }

  const expectsValue = () => {
    const top = stack.at(-1)
    if (!top) return !root.done
    return top.state === 'value' || top.state === 'value-or-close'
  }
  const valueEnded = () => {
    const top = stack.at(-1)
    if (top) top.state = 'comma-or-close'
    else root.done = true
  }

  for (const token of tokens) {
    const top = stack.at(-1)
    if (token === '{' || token === '[') {
      if (!expectsValue()) return false
      stack.push({ open: token, state: token === '{' ? 'key-or-close' : 'value-or-close' })
    } else if (token === '}' || token === ']') {
      if (top?.open !== (token === '}' ? '{' : '[')) return false
      if (top.state !== 'comma-or-close' && top.state !== 'key-or-close' && top.state !== 'value-or-close') return false
      stack.pop()
      valueEnded()
    } else if (token === ':') {
      if (top?.state !== 'colon') return false
      top.state = 'value'
    } else if (token === ',') {
      if (top?.state !== 'comma-or-close') return false
      top.state = top.open === '{' ? 'key' : 'value'
    } else if (top?.open === '{' && (top.state === 'key-or-close' || top.state === 'key')) {
      if (!token.startsWith('"')) return false
      top.state = 'colon'
    } else {
      if (!expectsValue()) return false
      valueEnded()
    }
  }
  return root.done && stack.length === 0
}

/** JSON re-indented by two spaces, or null if `raw` is not well-formed JSON. */
export function prettyJson(raw: string): CodeLine[] | null {
  const tokens = tokeniseJson(raw)
  if (!tokens || !isWellFormedJson(tokens)) return null

  const out = new LineBuilder()
  let depth = 0
  for (let i = 0; i < tokens.length; i++) {
    const token = tokens[i] ?? ''
    const next = tokens[i + 1]
    if (token === '{' || token === '[') {
      const close = token === '{' ? '}' : ']'
      if (next === close) {
        out.push('punct', token + close)
        i++
      } else {
        out.push('punct', token)
        depth++
        out.break(depth)
      }
    } else if (token === '}' || token === ']') {
      depth = Math.max(0, depth - 1)
      out.break(depth)
      out.push('punct', token)
    } else if (token === ',') {
      out.push('punct', ',')
      out.break(depth)
    } else if (token === ':') {
      out.push('punct', ': ')
    } else if (token.startsWith('"')) {
      out.push(next === ':' ? 'name' : 'string', token)
    } else {
      out.push('value', token)
    }
  }
  return depth === 0 ? out.done() : null
}

const XML_PART = /<\?[\s\S]*?\?>|<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<\/[^<>]+>|<[^<>]+>|[^<]+/gy
const XML_ATTRIBUTE = /(\s+)([^\s=/>]+)(?:(\s*=\s*)("[^"]*"|'[^']*'))?/y

type XmlPart =
  | { type: 'meta'; text: string }
  | { type: 'open'; name: string; rest: string }
  | { type: 'self'; name: string; rest: string }
  | { type: 'close'; name: string }
  | { type: 'text'; text: string }

function partsOfXml(raw: string): XmlPart[] | null {
  const parts: XmlPart[] = []
  let consumed = 0
  for (const match of raw.matchAll(XML_PART)) {
    const text = match[0]
    consumed += text.length
    if (text.startsWith('<?') || text.startsWith('<!')) parts.push({ type: 'meta', text })
    else if (text.startsWith('</')) parts.push({ type: 'close', name: text.slice(2, -1).trim() })
    else if (text.startsWith('<')) {
      const self = text.endsWith('/>')
      const inner = text.slice(1, self ? -2 : -1)
      const name = /^[^\s/>]+/.exec(inner)?.[0]
      if (!name) return null
      const rest = inner.slice(name.length)
      parts.push(self ? { type: 'self', name, rest } : { type: 'open', name, rest })
    } else if (text.trim().length > 0) parts.push({ type: 'text', text: text.trim() })
  }
  // The scan is sticky: a stray "<" stops it early, and a partial parse must not pass for the body.
  return consumed === raw.length ? parts : null
}

function pushTag(out: LineBuilder, part: Extract<XmlPart, { type: 'open' | 'self' }>) {
  out.push('punct', '<')
  out.push('name', part.name)
  const rest = part.rest.replace(/\s+$/, '')
  XML_ATTRIBUTE.lastIndex = 0
  let position = 0
  while (position < rest.length) {
    XML_ATTRIBUTE.lastIndex = position
    const match = XML_ATTRIBUTE.exec(rest)
    if (!match) {
      out.push('attr', rest.slice(position))
      break
    }
    out.push('attr', ` ${match[2] ?? ''}`)
    if (match[3] !== undefined) {
      out.push('punct', '=')
      out.push('string', match[4] ?? '')
    }
    position = XML_ATTRIBUTE.lastIndex
  }
  out.push('punct', part.type === 'self' ? '/>' : '>')
}

function pushClose(out: LineBuilder, name: string) {
  out.push('punct', '</')
  out.push('name', name)
  out.push('punct', '>')
}

/** XML with one element per line and leaf elements kept inline, or null if `raw` is not XML. */
export function prettyXml(raw: string): CodeLine[] | null {
  if (!raw.trimStart().startsWith('<')) return null
  const parts = partsOfXml(raw)
  if (!parts || parts.length === 0) return null

  const out = new LineBuilder()
  let depth = 0
  for (let i = 0; i < parts.length; i++) {
    const part = parts[i]
    if (!part) continue
    out.break(depth)
    if (part.type === 'meta') {
      out.push('meta', part.text)
    } else if (part.type === 'self') {
      pushTag(out, part)
    } else if (part.type === 'open') {
      pushTag(out, part)
      const next = parts[i + 1]
      const after = parts[i + 2]
      if (next?.type === 'close' && next.name === part.name) {
        pushClose(out, part.name)
        i++
      } else if (next?.type === 'text' && after?.type === 'close' && after.name === part.name) {
        out.push('value', next.text)
        pushClose(out, part.name)
        i += 2
      } else {
        depth++
      }
    } else if (part.type === 'close') {
      depth = Math.max(0, depth - 1)
      out.break(depth)
      pushClose(out, part.name)
    } else {
      out.push('value', part.text)
    }
  }
  return out.done()
}

function plain(raw: string): CodeLine[] {
  return raw.split(/\r?\n/).map((line) => ({ indent: 0, tokens: [{ kind: 'plain' as const, text: line }] }))
}

/** The body as lines to render: formatted by its content type when it parses, raw otherwise. */
export function highlightBody(body: string, contentType: string): CodeLine[] {
  if (body.length === 0) return []
  if (body.length <= MAX_HIGHLIGHT_CHARS) {
    const type = contentType.toLowerCase()
    const formatted = type.includes('json') ? prettyJson(body) : type.includes('xml') ? prettyXml(body) : null
    // Indentation is rendered as spaces, so absurd nesting would mean megabytes of whitespace.
    if (formatted?.every((line) => line.indent <= MAX_INDENT)) return formatted
  }
  return plain(body)
}
