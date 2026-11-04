#!/usr/bin/env bash
# Tests for deploy/mask-env.sh: every .env value of 4 or more characters is replaced with *** in the masked output, as
# Compose reads it and as the api's JSON logs escape it, whatever characters it holds. Everything else passes through
# unchanged, and an .env that cannot be parsed with certainty withholds the output.
#
#   bash deploy/tests/mask-env.test.sh
#
# Needs bash and awk only (CI runs it on ubuntu-latest, next to shellcheck).

# shellcheck disable=SC2016 # the literal $ in the .env and log samples is what these tests are about
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
masker="$here/../mask-env.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0
fail() { printf 'FAIL %s\n' "$*"; failures=$((failures + 1)); }

# must_not_leak <file> <value>...: none of the values appears in the file.
must_not_leak() {
  local file="$1" v
  shift
  for v in "$@"; do
    if grep -qF -- "$v" "$file"; then fail "$(basename "$file"): the value '$v' leaked"; fi
  done
}
# expect_line <file> <line>: the file has exactly this line.
expect_line() {
  grep -qxF -- "$2" "$1" || fail "$(basename "$1"): expected the line '$2'"
}
records() { awk 'END { print NR }' "$1"; }

# --- 1. Quoting styles and characters that would break a naive sed or regex replacement --------------------------
# A quoted and an exported value, an inline comment, one value that contains another, one with '=', a CRLF line and a
# 3-character value (too short to mask). Backslashes and '$' are literal only inside single quotes.
regex_value='p@ss.w*rd[0]^$|(x)+?{2}\1&/\back\slash'
cat > "$work/styles.env" <<ENV
# a comment
POSTGRES_PASSWORD=Sup3rS3cretPg

export ADMIN_API_KEY="adm-key/with+slash&amp"
GRAFANA_ADMIN_PASSWORD='single quoted pw'
TICKPULSE_SUBNET=172.31.230.0/24          # inline comment
REGEX_VALUE='$regex_value'
LONG_VALUE=Sup3rS3cretPgAndMore
WITH_EQUALS=a=b=c=d
CRLF_VALUE=crlf-secret-value$(printf '\r')
SHORT=abc
EMPTY=
EMPTY_QUOTED=""
ENV

{
  printf 'api-1  | Host=db;Password=%s;Maximum Pool Size=20\n' 'Sup3rS3cretPg'
  printf 'api-1  | X-Api-Key: %s accepted\n' 'adm-key/with+slash&amp'
  printf 'api-1  | grafana %s twice %s\n' 'single quoted pw' 'single quoted pw'
  printf 'api-1  | network %s\n' '172.31.230.0/24'
  printf 'api-1  | odd %s end\n' "$regex_value"
  printf 'api-1  | longer %s\n' 'Sup3rS3cretPgAndMore'
  printf 'api-1  | equals %s\n' 'a=b=c=d'
  printf 'api-1  | crlf %s\n' 'crlf-secret-value'
  printf 'api-1  | short abc stays\n'
  printf 'api-1  | plain line, nothing secret\n'
  printf 'api-1  | no trailing newline %s' 'Sup3rS3cretPg'
} > "$work/styles.log"

bash "$masker" "$work/styles.env" < "$work/styles.log" > "$work/styles.out"
must_not_leak "$work/styles.out" 'Sup3rS3cretPg' 'adm-key/with+slash&amp' 'single quoted pw' '172.31.230.0/24' \
  "$regex_value" 'Sup3rS3cretPgAndMore' 'a=b=c=d' 'crlf-secret-value'
expect_line "$work/styles.out" 'api-1  | Host=db;Password=***;Maximum Pool Size=20'
expect_line "$work/styles.out" 'api-1  | X-Api-Key: *** accepted'
expect_line "$work/styles.out" 'api-1  | grafana *** twice ***'
expect_line "$work/styles.out" 'api-1  | network ***'
expect_line "$work/styles.out" 'api-1  | odd *** end'
expect_line "$work/styles.out" 'api-1  | longer ***'
expect_line "$work/styles.out" 'api-1  | equals ***'
expect_line "$work/styles.out" 'api-1  | crlf ***'
expect_line "$work/styles.out" 'api-1  | short abc stays'
expect_line "$work/styles.out" 'api-1  | plain line, nothing secret'
expect_line "$work/styles.out" 'api-1  | no trailing newline ***'
[ "$(records "$work/styles.out")" -eq "$(records "$work/styles.log")" ] || fail "styles: the number of lines changed"

# --- 2. Compose's reading of the value is what gets masked ---------------------------------------------------------
# A: \" is unescaped. C: the comment after the closing quote holds quotes of its own. D: $$ is a literal '$'.
# S: \\ and \$ are unescaped.
cat > "$work/compose.env" <<'ENV'
A="pa\"ss-word1"
C="secret2" # the "main" one
D=pa$$word99
S="back\\slash\$dollar"
ENV
{
  printf '%s\n' 'api-1  | quote pa"ss-word1 end'
  printf '%s\n' 'api-1  | json {"@m":"pa\"ss-word1"}'
  printf '%s\n' 'api-1  | comment secret2 and the "main" one'
  printf '%s\n' 'api-1  | dollar pa$word99 end'
  printf '%s\n' 'api-1  | escapes back\slash$dollar end'
} > "$work/compose.log"
bash "$masker" "$work/compose.env" < "$work/compose.log" > "$work/compose.out"
must_not_leak "$work/compose.out" 'pa"ss-word1' 'ss-word1' 'secret2' 'pa$word99' 'word99' 'back\slash$dollar' 'slash'
expect_line "$work/compose.out" 'api-1  | quote *** end'
expect_line "$work/compose.out" 'api-1  | json {"@m":"***"}'
expect_line "$work/compose.out" 'api-1  | comment *** and the "main" one'
expect_line "$work/compose.out" 'api-1  | dollar *** end'
expect_line "$work/compose.out" 'api-1  | escapes *** end'

# --- 3. The api's own log lines (Serilog RenderedCompactJsonFormatter) --------------------------------------------
# fixtures/serilog-events.jsonl is the formatter's real output for two events that carry these secrets in the
# message, a property and an exception; DeployLogFixtureTests keeps it identical to what the formatter writes.
# JSON escapes '"' and '\', and the rendered message quotes string properties first, so each secret appears in up
# to three spellings.
cat > "$work/serilog.env" <<'ENV'
POSTGRES_PASSWORD="pa\"ss\\wo\"rd-9"
ADMIN_API_KEY='key\with"quote'
ENV
bash "$masker" "$work/serilog.env" < "$here/fixtures/serilog-events.jsonl" > "$work/serilog.out"
must_not_leak "$work/serilog.out" 'pa"ss\wo"rd-9' 'pa\"ss\\wo\"rd-9' 'pa\\\"ss\\wo\\\"rd-9' \
  'key\with"quote' 'key\\with\"quote' 'key\\with\\\"quote' 'rd-9' 'quote'
grep -qF '"ConnectionString":"Host=db;Port=5432;Database=tickpulse;Username=tickpulse;Password=***;Maximum Pool Size=20"' \
  "$work/serilog.out" || fail "serilog: the masked property is not intact JSON"
grep -qF '"@m":"Admin request with key \"***\" rejected"' "$work/serilog.out" || fail "serilog: the masked message is not intact"
[ "$(records "$work/serilog.out")" -eq 2 ] || fail "serilog: expected 2 lines"

# --- 4. Overlapping values are masked as one span ------------------------------------------------------------------
printf 'FIRST=abcdefgh\nSECOND=efghijkl\n' > "$work/overlap.env"
printf 'x abcdefghijkl y\nx efghijklabcdefgh y\n' > "$work/overlap.log"
bash "$masker" "$work/overlap.env" < "$work/overlap.log" > "$work/overlap.out"
must_not_leak "$work/overlap.out" 'abcd' 'ijkl' 'efgh'
expect_line "$work/overlap.out" 'x *** y'
[ "$(grep -c '^x \*\*\* y$' "$work/overlap.out")" -eq 2 ] || fail "overlap: expected both lines as 'x *** y'"

# --- 5. An empty .env passes the log through unchanged (and is not mistaken for the log itself) ---------------------
: > "$work/empty.env"
printf 'line one\nline two\n' > "$work/plain"
bash "$masker" "$work/empty.env" < "$work/plain" > "$work/plain.out"
cmp -s "$work/plain" "$work/plain.out" || fail "an empty .env changed the output"

# --- 6. Fail closed: what cannot be read with certainty withholds the output --------------------------------------
# withheld <name> <env content>: exit 2, nothing on stdout, a reason on stderr that does not quote the value.
withheld() {
  local name="$1" status=0
  printf '%s\n' "$2" > "$work/$name.env"
  bash "$masker" "$work/$name.env" < "$work/styles.log" > "$work/$name.out" 2> "$work/$name.err" || status=$?
  [ "$status" -eq 2 ] || fail "$name: exit $status, expected 2"
  [ -s "$work/$name.out" ] && fail "$name: output was let through"
  grep -q 'withheld' "$work/$name.err" || fail "$name: no reason given"
  must_not_leak "$work/$name.err" 'hidden-secret'
}
withheld multiline $'M="hidden-secret line one\nhidden-secret line two"'
withheld unclosed-single "U='hidden-secret"
withheld interpolation 'I=hidden-secret$HOME'
withheld braced-interpolation 'I="hidden-secret${HOME}"'
withheld unknown-escape 'E="hidden-secret\n"'
withheld escaped-backslash-before-quote 'E="hidden-secret\\"'
withheld text-after-quote 'T="hidden-secret"trailing'
withheld backslash-unquoted 'B=hidden\secret'
withheld quote-unquoted 'Q=hidden"secret'
withheld comment-only-value 'H=#hidden-secret'
withheld no-equals 'hidden-secret'
withheld bad-key 'BAD KEY=hidden-secret'

if bash "$masker" "$work/missing.env" < "$work/styles.log" > "$work/missing.out" 2> "$work/missing.err"; then
  fail "a missing .env did not fail"
fi
[ -s "$work/missing.out" ] && fail "a missing .env let output through"
grep -q 'withheld' "$work/missing.err" || fail "a missing .env gave no reason"

if [ "$failures" -ne 0 ]; then
  printf '%d check(s) failed\n' "$failures"
  exit 1
fi
echo "mask-env: all checks passed"
