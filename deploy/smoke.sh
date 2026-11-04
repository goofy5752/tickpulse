#!/usr/bin/env bash
# TickPulse smoke test: the public surface of a running deployment, end to end.
#
#   ./smoke.sh <base-url> [wait-seconds]
#
#   ./smoke.sh http://127.0.0.1:8085          the api container, from the server itself (what deploy.sh runs)
#   ./smoke.sh https://tickpulse.dev          through Cloudflare and the host nginx
#   ./smoke.sh http://127.0.0.1:8085 120      retry the whole suite for up to 120 s until it passes
#
# The optional wait covers a stack that has only just started, such as a first deploy on an empty database: the
# first trades and the first complete 1-minute candle need a minute or two.
#
# Checks:
#   1. GET /health/ready                                 200
#   2. GET /api/BTCUSDT/24hAvgPrice                      200 application/json with "sampleCount"
#   3. the same with Accept: application/xml             200 application/xml with <sampleCount>
#   4. GET /api/BTCUSDT/SimpleMovingAverage?n=5&p=1m     200 application/json
#   5. GET /api/NOPEUSDT/24hAvgPrice, Accept xml         404 application/problem+xml (unknown-symbol)
#   6. GET /                                             200 text/html, the dashboard's index.html
#
# Exits 0 when every check passes, otherwise 1 with one FAIL line per failed check (2 on bad usage).
# Needs only bash, curl and grep.
set -uo pipefail

usage() {
  echo "usage: $0 <base-url> [wait-seconds]" >&2
  exit 2
}

[ $# -ge 1 ] && [ $# -le 2 ] || usage
base="${1%/}"
wait_seconds="${2:-0}"
case "$base" in http://* | https://*) ;; *) usage ;; esac
case "$wait_seconds" in '' | *[!0-9]*) usage ;; esac

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0

# check <name> <path> <accept> <status> <content-type> [<body-pattern>]
#   <content-type> is compared with the media type of the response, without parameters (charset).
#   <body-pattern> is an extended regex the body must match.
check() {
  local name="$1" path="$2" accept="$3" want_status="$4" want_type="$5" pattern="${6:-}"
  local body="$work/body" out status type

  if ! out="$(curl -sS --max-time 15 -o "$body" -w '%{http_code} %{content_type}' \
    -H "Accept: $accept" "$base$path" 2>&1)"; then
    echo "FAIL  $name: GET $path did not complete: $out"
    failures=$((failures + 1))
    return
  fi

  status="${out%% *}"
  type="${out#* }"
  type="${type%%;*}"

  if [ "$status" != "$want_status" ]; then
    echo "FAIL  $name: GET $path returned $status, expected $want_status"
  elif [ "$type" != "$want_type" ]; then
    echo "FAIL  $name: GET $path returned Content-Type '$type', expected '$want_type'"
  elif [ -n "$pattern" ] && ! grep -Eq "$pattern" "$body"; then
    echo "FAIL  $name: GET $path body does not match /$pattern/"
  else
    echo "ok    $name"
    return
  fi
  failures=$((failures + 1))
}

run_suite() {
  failures=0
  check "ready" /health/ready application/json 200 application/json '"status":"Healthy"'
  check "24h average, JSON" /api/BTCUSDT/24hAvgPrice application/json 200 application/json '"sampleCount":[0-9]+'
  check "24h average, XML" /api/BTCUSDT/24hAvgPrice application/xml 200 application/xml '<sampleCount>[0-9]+</sampleCount>'
  check "SMA n=5 p=1m" '/api/BTCUSDT/SimpleMovingAverage?n=5&p=1m' application/json 200 application/json '"symbol":"BTCUSDT"'
  check "unknown symbol, XML problem" /api/NOPEUSDT/24hAvgPrice application/xml 404 application/problem+xml 'problems/unknown-symbol'
  check "dashboard" / text/html 200 text/html '<div id="root">'
}

echo "Smoke test of $base"
deadline=$((SECONDS + wait_seconds))
while :; do
  run_suite
  if [ "$failures" -eq 0 ]; then
    echo "PASS  all checks passed against $base"
    exit 0
  fi
  if [ "$SECONDS" -ge "$deadline" ]; then
    echo "FAILED  $failures check(s) failed against $base" >&2
    exit 1
  fi
  echo "... $failures check(s) failed; retrying in 10 s (until ${wait_seconds} s have passed)"
  sleep 10
done
