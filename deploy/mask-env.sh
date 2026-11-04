#!/usr/bin/env bash
# Copies stdin to stdout with every value of <env-file> (4 characters or longer) replaced by ***.
#
#   docker compose ... logs api | ./mask-env.sh .env
#
# deploy.sh pipes the api log through it before the log reaches deploy.log, which the CD job streams into the
# GitHub Actions log. The .env values (POSTGRES_PASSWORD, ADMIN_API_KEY, ...) are not GitHub secrets, so nothing
# else would mask them there.
#
# The matching is literal (awk index/substr, no regex), so a value may hold any character. Each value is masked as
# written after '=' and as Compose reads it (without an `export` prefix, quotes, or an inline " # comment"). Longer
# values are replaced first, so a value that contains another one is masked whole. Values shorter than 4
# characters are left alone; they would mostly hide ordinary words. If <env-file> cannot be read, nothing is
# printed and the exit code is 2: the output is withheld rather than shown unmasked.
set -euo pipefail

[ $# -eq 1 ] || { echo "usage: $0 <env-file> < input > output" >&2; exit 2; }
env_file="$1"
if [ ! -r "$env_file" ] || [ -d "$env_file" ]; then
  echo "mask-env: cannot read $env_file; output withheld" >&2
  exit 2
fi

# The env file is read first (FILENAME == ARGV[1], which also works when it is empty), then stdin ("-").
exec awk '
  function add(v,   i, j) {
    if (length(v) < 4 || (v in seen)) return
    seen[v] = 1
    # Insertion sort, longest first.
    for (i = n; i >= 1 && length(vals[i]) < length(v); i--) vals[i + 1] = vals[i]
    vals[i + 1] = v
    n++
  }
  function mask(s,   k, i, out, v) {
    for (k = 1; k <= n; k++) {
      v = vals[k]
      out = ""
      while ((i = index(s, v)) > 0) {
        out = out substr(s, 1, i - 1) "***"
        s = substr(s, i + length(v))
      }
      s = out s
    }
    return s
  }
  FILENAME == ARGV[1] {
    line = $0
    sub(/\r$/, "", line)
    if (line ~ /^[ \t]*(#|$)/) next
    sub(/^[ \t]*export[ \t]+/, "", line)
    eq = index(line, "=")
    if (eq == 0) next
    raw = substr(line, eq + 1)
    sub(/[ \t]+$/, "", raw)
    add(raw)
    v = raw
    sub(/^[ \t]+/, "", v)
    q = substr(v, 1, 1)
    if (q == "\"" || q == "\047") {
      # Quoted: up to the last matching quote.
      v = substr(v, 2)
      for (i = length(v); i > 0 && substr(v, i, 1) != q; i--) ;
      if (i > 0) v = substr(v, 1, i - 1)
    } else {
      sub(/[ \t]+#.*$/, "", v)
    }
    add(v)
    next
  }
  { print mask($0) }
' "$env_file" -
