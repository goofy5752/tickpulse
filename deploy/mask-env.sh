#!/usr/bin/env bash
# Copies stdin to stdout with every value of <env-file> (4 characters or longer) replaced by ***.
#
#   docker compose ... logs api | ./mask-env.sh .env
#
# deploy.sh pipes the api log through it before the log reaches deploy.log, which the CD job streams into the
# GitHub Actions log. The .env values (POSTGRES_PASSWORD, ADMIN_API_KEY, ...) are not GitHub secrets, so nothing
# else would mask them there.
#
# What is masked, for each value:
#   - the value as Compose reads it, and as written in the file;
#   - its JSON spelling ('"' as \", '\' as \\, tab as \t), as the api's Serilog JSON writes it in a property or an
#     exception;
#   - the JSON spelling of the value with '"' first written as \", as it appears inside the rendered message, where
#     Serilog quotes string properties.
# Matching is literal (awk index/substr, no regex). Every occurrence of every spelling is marked first and each
# marked span is replaced by one ***, so overlapping values cannot leave a part behind.
#
# The file is read the way Compose reads it, for the syntax below only. Anything else, where this script could
# read a different value than Compose does, stops it with exit code 2 and nothing on stdout: the output is
# withheld rather than shown unmasked. The reason goes to stderr, with the line number but never the value.
#   - blank lines and lines starting with #; an optional "export " prefix; KEY=value with KEY of [A-Za-z0-9_.-]
#   - 'single quoted': literal, on one line, no backslash just before the closing quote
#   - "double quoted": on one line; the escapes \" \\ \$ only, and no \\ just before the closing quote; $$ is '$';
#     any other $ (interpolation) is refused
#   - after a closing quote: nothing, or whitespace and a # comment
#   - unquoted: up to the first " #" (inline comment), trailing whitespace removed; $$ is '$'; any other $, a
#     backslash, a quote, a tab before #, a value starting with #, or a control character is refused
# Values shorter than 4 characters are left alone; they would mostly hide ordinary words.
set -euo pipefail

[ $# -eq 1 ] || { echo "usage: $0 <env-file> < input > output" >&2; exit 2; }
env_file="$1"
if [ ! -r "$env_file" ] || [ -d "$env_file" ]; then
  echo "mask-env: cannot read $env_file; output withheld" >&2
  exit 2
fi

# The env file is read first (FILENAME == ARGV[1], which also works when it is empty), then stdin ("-").
exec awk '
  BEGIN {
    BS = "\\"; DQ = "\""; SQ = "\047"
    # Control characters other than tab. Serilog writes them as \uXXXX, which is not reproduced here.
    for (c = 1; c < 32; c++) if (c != 9) CTRL = CTRL sprintf("%c", c)
  }

  function refuse(why) {
    printf "mask-env: %s line %d: %s; output withheld\n", ARGV[1], FNR, why > "/dev/stderr"
    exit 2
  }

  function keep(v) {
    if (length(v) < 4 || (v in seen)) return
    seen[v] = 1
    vals[++n] = v
  }

  # JSON string content, as Serilog escapes it.
  function json(s,   out, i, c) {
    out = ""
    for (i = 1; i <= length(s); i++) {
      c = substr(s, i, 1)
      if (c == BS) out = out BS BS
      else if (c == DQ) out = out BS DQ
      else if (c == "\t") out = out BS "t"
      else out = out c
    }
    return out
  }

  # A string property inside a rendered message: Serilog writes it as "..." with each " as \".
  function rendered(s,   out, i, c) {
    out = ""
    for (i = 1; i <= length(s); i++) {
      c = substr(s, i, 1)
      out = out (c == DQ ? BS DQ : c)
    }
    return out
  }

  # value: as Compose reads it; written: as it stands in the file.
  function add(value, written,   i) {
    if (length(value) < 4) return
    for (i = 1; i <= length(CTRL); i++) {
      if (index(value, substr(CTRL, i, 1))) refuse("a value holds a control character")
    }
    keep(value)
    keep(written)
    keep(json(value))
    keep(json(rendered(value)))
  }

  # What may follow a closing quote.
  function after_quote(s) {
    if (s ~ /^[ \t]*$/ || s ~ /^[ \t]+#/) return
    refuse("text after the closing quote")
  }

  function double_quoted(s,   len, i, c, d, v) {
    len = length(s)
    v = ""
    for (i = 2; i <= len; i++) {
      c = substr(s, i, 1)
      if (c == BS) {
        d = substr(s, i + 1, 1)
        if (d == DQ || d == "$") {
          v = v d
        } else if (d == BS) {
          # Compose ends the value at the first quote that has no backslash just before it.
          if (substr(s, i + 2, 1) == DQ) refuse("a doubled backslash before a quote")
          v = v BS
        } else {
          refuse("an escape other than the escaped quote, backslash and dollar")
        }
        i++
      } else if (c == "$") {
        if (substr(s, i + 1, 1) != "$") refuse("variable interpolation")
        v = v "$"
        i++
      } else if (c == DQ) {
        after_quote(substr(s, i + 1))
        add(v, substr(s, 2, i - 2))
        return
      } else {
        v = v c
      }
    }
    refuse("an unclosed double quote (values over several lines are not supported)")
  }

  function single_quoted(s,   i, v) {
    i = index(substr(s, 2), SQ)
    if (i == 0) refuse("an unclosed single quote (values over several lines are not supported)")
    v = substr(s, 2, i - 1)
    if (substr(v, length(v), 1) == BS) refuse("a backslash before the closing quote")
    after_quote(substr(s, i + 2))
    add(v, v)
  }

  function unquoted(s,   len, i, c, p, raw, v) {
    if (substr(s, 1, 1) == "#") refuse("a value that starts with #")
    len = length(s)
    raw = ""
    for (i = 1; i <= len; i++) {
      c = substr(s, i, 1)
      if (c == "#") {
        p = substr(s, i - 1, 1)
        if (p == " ") break
        if (p == "\t") refuse("a tab before #")
      }
      raw = raw c
    }
    sub(/[ \t]+$/, "", raw)
    len = length(raw)
    v = ""
    for (i = 1; i <= len; i++) {
      c = substr(raw, i, 1)
      if (c == BS || c == DQ || c == SQ) refuse("a backslash or quote in an unquoted value")
      if (c == "$") {
        if (substr(raw, i + 1, 1) != "$") refuse("variable interpolation")
        i++
      }
      v = v c
    }
    add(v, raw)
  }

  FILENAME == ARGV[1] {
    line = $0
    sub(/\r$/, "", line)
    if (line ~ /^[ \t]*(#.*)?$/) next
    sub(/^[ \t]+/, "", line)
    sub(/^export[ \t]+/, "", line)
    eq = index(line, "=")
    if (eq == 0) refuse("not a KEY=value line")
    key = substr(line, 1, eq - 1)
    sub(/[ \t]+$/, "", key)
    if (key !~ /^[A-Za-z_][A-Za-z0-9_.-]*$/) refuse("not a valid key")
    rest = substr(line, eq + 1)
    sub(/^[ \t]+/, "", rest)
    q = substr(rest, 1, 1)
    if (q == DQ) double_quoted(rest)
    else if (q == SQ) single_quoted(rest)
    else unquoted(rest)
    next
  }

  # Mark every occurrence of every spelling, then write each marked span as one ***.
  function mask(s,   len, k, v, lv, start, i, p, j, out, any) {
    len = length(s)
    split("", M)
    any = 0
    for (k = 1; k <= n; k++) {
      v = vals[k]
      lv = length(v)
      start = 1
      while (start + lv - 1 <= len && (i = index(substr(s, start), v)) > 0) {
        p = start + i - 1
        for (j = p; j < p + lv; j++) M[j] = 1
        any = 1
        start = p + 1
      }
    }
    if (!any) return s
    out = ""
    i = 1
    while (i <= len) {
      if (i in M) {
        out = out "***"
        while (i in M) i++
      } else {
        j = i
        while (j <= len && !(j in M)) j++
        out = out substr(s, i, j - i)
        i = j
      }
    }
    return out
  }

  { print mask($0) }
' "$env_file" -
