import { describe, expect, it } from 'vitest'
import { highlightBody, prettyJson, prettyXml, type CodeLine } from './highlight'

const text = (lines: CodeLine[] | null) =>
  (lines ?? []).map((line) => '  '.repeat(line.indent) + line.tokens.map((t) => t.text).join('')).join('\n')

describe('prettyJson', () => {
  it('indents objects and keeps the exact number text', () => {
    const lines = prettyJson('{"symbol":"BTCUSDT","value":1.50000000,"ok":true,"none":null}')
    expect(text(lines)).toBe(
      ['{', '  "symbol": "BTCUSDT",', '  "value": 1.50000000,', '  "ok": true,', '  "none": null', '}'].join('\n'),
    )
  })

  it('keeps empty containers on one line and nests arrays', () => {
    const lines = prettyJson('[{"a":[]},{"b":{}}]')
    expect(text(lines)).toBe(['[', '  {', '    "a": []', '  },', '  {', '    "b": {}', '  }', ']'].join('\n'))
  })

  it('tells keys from string values', () => {
    const tokens = prettyJson('{"k":"v"}')?.[1]?.tokens ?? []
    expect(tokens.map((t) => t.kind)).toEqual(['name', 'punct', 'string'])
  })

  it('handles escaped quotes inside strings', () => {
    expect(text(prettyJson('{"d":"say \\"hi\\""}'))).toContain('"d": "say \\"hi\\""')
  })

  it('returns null for something that is not JSON', () => {
    expect(prettyJson('<html>')).toBeNull()
    expect(prettyJson('{"a": undefined}')).toBeNull()
  })

  it('returns null for mismatched brackets', () => {
    expect(prettyJson('{]')).toBeNull()
    expect(prettyJson('[1}')).toBeNull()
    expect(prettyJson('[1]]')).toBeNull()
    expect(prettyJson('{"a":[1}')).toBeNull()
  })

  it('returns null for tokens out of grammar order', () => {
    expect(prettyJson('{"a" "b"}')).toBeNull()
    expect(prettyJson('{"a":1,}')).toBeNull()
    expect(prettyJson('{1:2}')).toBeNull()
    expect(prettyJson('[1 2]')).toBeNull()
    expect(prettyJson('{"a"::1}')).toBeNull()
    expect(prettyJson('1 2')).toBeNull()
  })

  it('accepts scalars and deep nesting without recursion', () => {
    expect(text(prettyJson('"x"'))).toBe('"x"')
    const deep = '['.repeat(20_000) + ']'.repeat(20_000)
    expect(prettyJson(deep)).not.toBeNull()
  })
})

describe('prettyXml', () => {
  it('puts leaf elements on one line and indents children', () => {
    const lines = prettyXml('<R xmlns="urn:x"><A>1</A><B><C>2</C></B><D i:nil="true"/></R>')
    expect(text(lines)).toBe(
      ['<R xmlns="urn:x">', '  <A>1</A>', '  <B>', '    <C>2</C>', '  </B>', '  <D i:nil="true"/>', '</R>'].join('\n'),
    )
  })

  it('tokenises tag names, attributes and values', () => {
    const tokens = prettyXml('<R a="1">x</R>')?.[0]?.tokens ?? []
    expect(tokens.map((t) => [t.kind, t.text])).toEqual([
      ['punct', '<'],
      ['name', 'R'],
      ['attr', ' a'],
      ['punct', '='],
      ['string', '"1"'],
      ['punct', '>'],
      ['value', 'x'],
      ['punct', '</'],
      ['name', 'R'],
      ['punct', '>'],
    ])
  })

  it('keeps the declaration and an empty element', () => {
    expect(text(prettyXml('<?xml version="1.0"?><R><E></E></R>'))).toBe(
      ['<?xml version="1.0"?>', '<R>', '  <E></E>', '</R>'].join('\n'),
    )
  })

  it('returns null for something that is not XML', () => {
    expect(prettyXml('{"a":1}')).toBeNull()
  })
})

describe('highlightBody', () => {
  it('picks the formatter from the content type', () => {
    expect(text(highlightBody('{"a":1}', 'application/problem+json'))).toBe('{\n  "a": 1\n}')
    expect(text(highlightBody('<a>1</a>', 'application/xml; charset=utf-8'))).toBe('<a>1</a>')
  })

  it('falls back to the raw text when the body does not parse', () => {
    expect(text(highlightBody('not json', 'application/json'))).toBe('not json')
    expect(text(highlightBody('a\nb', 'text/plain'))).toBe('a\nb')
  })

  it('shows absurdly deep nesting as plain text', () => {
    const deep = '['.repeat(100) + ']'.repeat(100)
    expect(highlightBody(deep, 'application/json')).toEqual([{ indent: 0, tokens: [{ kind: 'plain', text: deep }] }])
  })

  it('returns no lines for an empty body', () => {
    expect(highlightBody('', 'application/json')).toEqual([])
  })
})
