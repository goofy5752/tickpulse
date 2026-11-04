#!/usr/bin/env bash
# TickPulse deploy with automatic rollback. Runs on the server, as the deploy user.
#
#   .incoming/deploy.sh --install <tag>   what the CD job runs: install the files it uploaded to
#                                         /srv/tickpulse/.incoming/, then deploy <tag> from /srv/tickpulse
#   ./deploy.sh <tag>                     deploy ghcr.io/goofy5752/tickpulse-api:<tag> (a commit sha) with the files
#                                         already in place; also the manual way to roll back:
#   ./deploy.sh "$(cat .last_good_tag)"   redeploy the last tag that passed the smoke test
#
# Steps:
#   0. (--install) move docker-compose.prod.yml, smoke.sh, deploy.sh and observability/ into place;
#      observability/ is replaced only when its content changed, and then its containers are recreated
#      (compose cannot see changes inside bind-mounted config files)
#   1. pull every image for <tag>; a failed pull stops here and the running stack stays as it is
#   2. up -d --remove-orphans
#   3. wait up to READY_TIMEOUT seconds (90) for /health/ready on 127.0.0.1:8085
#   4. ./smoke.sh http://127.0.0.1:8085 with SMOKE_WAIT (120) seconds of retries, which covers a first start on
#      an empty database
#   5. success: write <tag> to .last_good_tag, delete older api images, exit 0
#      failure: redeploy the tag in .last_good_tag (steps 2-4), then exit 1 even when that works
#
# A rollback restores the api image, not the files: observability/ and the compose file stay at the new version.
#
# Exit codes: 0 deployed; 1 failed (rolled back, or nothing to roll back to); 2 bad usage or setup;
# 3 failed and the rollback failed too.
#
# SKIP_PULL=1 skips step 1, for a tag that exists only in the local image store (testing this script).
set -euo pipefail

readonly COMPOSE_FILE=docker-compose.prod.yml
readonly ENV_FILE=.env
readonly LAST_GOOD=.last_good_tag
# Set when observability/ was replaced and removed once its containers are recreated, so a deploy that stops
# early (a failed pull) leaves the recreate to the next one.
readonly RECREATE_MARKER=.recreate_observability
readonly API_URL=http://127.0.0.1:8085
readonly IMAGE=ghcr.io/goofy5752/tickpulse-api
readonly OBSERVABILITY_SERVICES=(otel-collector prometheus loki tempo grafana)
readonly READY_TIMEOUT="${READY_TIMEOUT:-90}"
readonly SMOKE_WAIT="${SMOKE_WAIT:-120}"

log() { printf '[deploy %s] %s\n' "$(date -u +%H:%M:%S)" "$*"; }
usage() { echo "usage: $0 [--install] <tag>" >&2; exit 2; }

install=0
if [ "${1:-}" = --install ]; then
  install=1
  shift
fi
[ $# -eq 1 ] || usage
tag="$1"
case "$tag" in '' | *[!A-Za-z0-9._-]*) echo "invalid tag '$tag'" >&2; exit 2 ;; esac

script_dir="$(cd "$(dirname "$0")" && pwd)"

if [ "$install" = 1 ]; then
  # The uploaded files sit in <deploy dir>/.incoming, next to the files they replace.
  [ "$(basename "$script_dir")" = .incoming ] || { echo "--install must run .incoming/deploy.sh" >&2; exit 2; }
  incoming="$script_dir"
  cd "$incoming/.."
  for f in "$COMPOSE_FILE" smoke.sh deploy.sh observability; do
    [ -e "$incoming/$f" ] || { echo "$incoming/$f is missing from the upload" >&2; exit 2; }
  done
  if [ -d observability ] && diff -rq observability "$incoming/observability" > /dev/null; then
    rm -rf "$incoming/observability"
  else
    rm -rf observability.previous
    if [ -d observability ]; then
      log "observability/ changed; its containers will be recreated"
      mv observability observability.previous
      touch "$RECREATE_MARKER"
    fi
    mv "$incoming/observability" observability
    rm -rf observability.previous
  fi
  # mv keeps the inode, so this running script is not disturbed by moving it.
  mv -f "$incoming/$COMPOSE_FILE" "$incoming/smoke.sh" "$incoming/deploy.sh" .
  chmod 755 smoke.sh deploy.sh
  rm -rf "$incoming"
  log "installed the uploaded files into $PWD"
else
  cd "$script_dir"
fi

[ -f "$ENV_FILE" ] || { echo "$PWD/$ENV_FILE is missing (see docs/deployment.md)" >&2; exit 2; }
[ -f "$COMPOSE_FILE" ] || { echo "$PWD/$COMPOSE_FILE is missing" >&2; exit 2; }

compose() { docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"; }

wait_ready() {
  local deadline=$((SECONDS + READY_TIMEOUT))
  until curl -fsS --max-time 5 -o /dev/null "$API_URL/health/ready" 2> /dev/null; do
    if [ "$SECONDS" -ge "$deadline" ]; then
      log "/health/ready did not return 200 within ${READY_TIMEOUT} s"
      return 1
    fi
    sleep 3
  done
  log "/health/ready is 200"
}

# start <tag>: up, ready, smoke. Returns non-zero at the first step that fails.
start() {
  export TICKPULSE_TAG="$1"
  log "starting tag $TICKPULSE_TAG"
  compose up -d --remove-orphans || return 1
  if [ -e "$RECREATE_MARKER" ]; then
    compose up -d --no-deps --force-recreate "${OBSERVABILITY_SERVICES[@]}" || return 1
    rm -f "$RECREATE_MARKER"
  fi
  wait_ready || return 1
  ./smoke.sh "$API_URL" "$SMOKE_WAIT" || return 1
}

# prune_api_images <keep>...: delete the other local tags of the api image (that repository only, nothing else on
# the host), so the server keeps the running tag and the rollback target.
prune_api_images() {
  local t k keep
  docker image ls "$IMAGE" --format '{{.Tag}}' | while read -r t; do
    keep=0
    for k in "$@" latest '<none>'; do [ "$t" = "$k" ] && keep=1; done
    [ "$keep" = 1 ] || docker image rm "$IMAGE:$t" > /dev/null 2>&1 || true
  done
}

previous=""
if [ -s "$LAST_GOOD" ]; then previous="$(tr -d '[:space:]' < "$LAST_GOOD")"; fi
log "deploying $tag (last good: ${previous:-none})"

if [ "${SKIP_PULL:-0}" != 1 ]; then
  export TICKPULSE_TAG="$tag"
  if ! compose pull --quiet; then
    log "pull failed; the running stack is unchanged"
    exit 1
  fi
fi

if start "$tag"; then
  printf '%s\n' "$tag" > "$LAST_GOOD"
  log "deployed $tag"
  prune_api_images "$tag" "$previous"
  exit 0
fi

log "tag $tag failed; last api log lines:"
compose logs --no-color --tail 50 api || true

if [ -z "$previous" ] || [ "$previous" = "$tag" ]; then
  log "no earlier good tag to roll back to"
  exit 1
fi

log "rolling back to $previous"
if start "$previous"; then
  log "rolled back to $previous; $tag is NOT deployed"
  exit 1
fi

log "rollback to $previous failed as well; the site may be down"
exit 3
