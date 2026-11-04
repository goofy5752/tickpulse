#!/usr/bin/env bash
# Tests for deploy/mask-env.sh: every .env value of 4 or more characters is replaced with *** in the masked output,
# whatever characters it holds, and everything else passes through unchanged.
#
#   bash deploy/tests/mask-env.test.sh
#
# Needs bash and awk only (CI runs it on ubuntu-latest, next to shellcheck).
set -euo pipefail

masker="$(cd "$(dirname "$0")/.." && pwd)/mask-env.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0
fail() { printf 'FAIL %s\n' "$*"; failures=$((failures + 1)); }

# Values that would break a naive sed or regex replacement, a quoted and an exported value, a value with an inline
# comment, one that contains another, one with '=', a CRLF line and a 3-character value (too short to mask).
regex_value='p@ss.w*rd[0]^$|(x)+?{2}\1&/\back\slash'
cat > "$work/.env" <<ENV
# a comment
POSTGRES_PASSWORD=Sup3rS3cretPg

export ADMIN_API_KEY="adm-key/with+slash&amp"
GRAFANA_ADMIN_PASSWORD='single quoted pw'
TICKPULSE_SUBNET=172.31.230.0/24          # inline comment
REGEX_VALUE=$regex_value
LONG_VALUE=Sup3rS3cretPgAndMore
WITH_EQUALS=a=b=c=d
CRLF_VALUE=crlf-secret-value$(printf '\r')
SHORT=abc
EMPTY=
ENV

values=(
  'Sup3rS3cretPg'
  'adm-key/with+slash&amp'
  'single quoted pw'
  '172.31.230.0/24'
  "$regex_value"
  'Sup3rS3cretPgAndMore'
  'a=b=c=d'
  'crlf-secret-value'
)

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
} > "$work/log"

bash "$masker" "$work/.env" < "$work/log" > "$work/masked"

for v in "${values[@]}"; do
  if grep -qF -- "$v" "$work/masked"; then fail "the value '$v' leaked"; fi
done

expect_line() {
  grep -qxF -- "$1" "$work/masked" || fail "expected the line '$1'"
}
expect_line 'api-1  | Host=db;Password=***;Maximum Pool Size=20'
expect_line 'api-1  | X-Api-Key: *** accepted'
expect_line 'api-1  | grafana *** twice ***'
expect_line 'api-1  | network ***'
expect_line 'api-1  | odd *** end'
expect_line 'api-1  | longer ***'
expect_line 'api-1  | equals ***'
expect_line 'api-1  | crlf ***'
expect_line 'api-1  | short abc stays'
expect_line 'api-1  | plain line, nothing secret'
expect_line 'api-1  | no trailing newline ***'

records() { awk 'END { print NR }' "$1"; }
[ "$(records "$work/masked")" -eq "$(records "$work/log")" ] || fail "the number of lines changed"

# An .env with no values to mask passes the log through unchanged (and is not mistaken for the log itself).
: > "$work/empty.env"
printf 'line one\nline two\n' > "$work/plain"
bash "$masker" "$work/empty.env" < "$work/plain" > "$work/plain.masked"
cmp -s "$work/plain" "$work/plain.masked" || fail "an empty .env changed the output"

# A missing .env withholds the output instead of passing it through unmasked.
if bash "$masker" "$work/missing.env" < "$work/log" > "$work/missing.out" 2> "$work/missing.err"; then
  fail "a missing .env did not fail"
fi
[ -s "$work/missing.out" ] && fail "a missing .env let output through"
grep -q 'withheld' "$work/missing.err" || fail "a missing .env gave no reason"

if [ "$failures" -ne 0 ]; then
  printf '%d check(s) failed\n' "$failures"
  exit 1
fi
echo "mask-env: all checks passed"
