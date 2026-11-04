#!/usr/bin/env bash
# TickPulse deploy with automatic rollback. Runs on the server, as the deploy user.
#
#   .incoming/deploy.sh --detach --install <sha>   what the CD job runs: in the background (immune to a dropped
#                                                  SSH session), install the files it uploaded to
#                                                  /srv/tickpulse/.incoming/ and deploy <sha>; the output goes
#                                                  to .incoming/deploy.log and the exit code to
#                                                  .incoming/deploy.exit, which the job polls
#   ./deploy.sh <tag>                              deploy ghcr.io/goofy5752/tickpulse-api:<tag> with the files
#                                                  already in place; also the manual way to roll back:
#   ./deploy.sh "$(cat .last_good_tag)"            redeploy the last tag that passed the smoke test
#
# Steps, stopping at the first failure:
#   1. take the deploy lock (.deploy.lock, flock); a second deploy refuses to start
#   2. validate: `docker compose config` of the compose file to be used (the uploaded one with --install), and
#      `bash -n` of the uploaded scripts. Nothing has changed yet, so a failure here changes nothing.
#   3. pull every image for <tag> with that compose file. A failure here also leaves everything as it was.
#   4. (--install) move the current compose file, scripts and observability/ to .previous/, and the uploaded
#      ones into place. observability/ is swapped with `mv` only when its content changed, and is never
#      deleted: the running containers keep reading the old files (same inodes) until step 5 recreates them.
#   5. up -d --remove-orphans, and recreate the observability containers when their config changed
#      (compose cannot see changes inside bind-mounted files)
#   6. wait up to READY_TIMEOUT seconds (90) for /health/ready on 127.0.0.1:8085
#   7. ./smoke.sh http://127.0.0.1:8085, retrying for SMOKE_WAIT seconds (120), which covers a first start on an
#      empty database
#   8. success: write <tag> to .last_good_tag, delete older api images, exit 0.
#      failure in steps 5-7: restore the files from .previous/ (with --install), redeploy the tag in
#      .last_good_tag (steps 5-7), then exit 1 even when that works.
#
# Exit codes: 0 deployed; 1 failed (rolled back, or nothing to roll back to); 2 refused (bad usage, invalid
# upload, missing setup, another deploy running), nothing changed; 3 failed and the rollback failed too.
#
# SKIP_PULL=1 skips step 3, for a tag that exists only in the local image store (testing this script).
set -euo pipefail

readonly COMPOSE_FILE=docker-compose.prod.yml
readonly ENV_FILE=.env
readonly LAST_GOOD=.last_good_tag
readonly LOCK_FILE=.deploy.lock
# The files that were live before the current deploy, kept for its rollback. It never holds config that a
# running container still reads (see install_upload), so it is safe to replace at the next deploy.
readonly PREVIOUS=.previous
# Set when observability/ was swapped, removed once its containers are recreated, so a deploy that stops early
# leaves the recreate to the next one.
readonly RECREATE_MARKER=.recreate_observability
readonly API_URL=http://127.0.0.1:8085
readonly IMAGE=ghcr.io/goofy5752/tickpulse-api
readonly SCRIPTS=(smoke.sh deploy.sh)
readonly OBSERVABILITY_SERVICES=(otel-collector prometheus loki tempo grafana)
readonly READY_TIMEOUT="${READY_TIMEOUT:-90}"
readonly SMOKE_WAIT="${SMOKE_WAIT:-120}"

log() { printf '[deploy %s] %s\n' "$(date -u +%H:%M:%S)" "$*"; }
refuse() { log "refused: $*; nothing was changed"; exit 2; }
usage() { echo "usage: $0 [--detach] [--install] <tag>" >&2; exit 2; }

detach=0
install=0
while [ $# -gt 0 ]; do
  case "$1" in
    --detach) detach=1 ;;
    --install) install=1 ;;
    -*) usage ;;
    *) break ;;
  esac
  shift
done
[ $# -eq 1 ] || usage
tag="$1"
case "$tag" in '' | *[!A-Za-z0-9._-]*) echo "invalid tag '$tag'" >&2; exit 2 ;; esac

script_dir="$(cd "$(dirname "$0")" && pwd)"

if [ "$detach" = 1 ]; then
  # Run this script again in its own session, so it survives the SSH connection. The exit code is written
  # atomically after the log is complete, so a reader that sees deploy.exit has the whole log.
  rm -f "$script_dir/deploy.exit" "$script_dir/deploy.exit.tmp"
  args=()
  if [ "$install" = 1 ]; then args+=(--install); fi
  # shellcheck disable=SC2016 # $0, $@ and the DEPLOY_* variables expand in the detached shell
  DEPLOY_LOG="$script_dir/deploy.log" DEPLOY_EXIT="$script_dir/deploy.exit" nohup setsid bash -c '
      bash "$0" "$@" > "$DEPLOY_LOG" 2>&1
      echo "$?" > "$DEPLOY_EXIT.tmp" && mv "$DEPLOY_EXIT.tmp" "$DEPLOY_EXIT"
    ' "$script_dir/deploy.sh" ${args[@]+"${args[@]}"} "$tag" < /dev/null > /dev/null 2>&1 &
  echo "deploy of $tag started in the background; log: $script_dir/deploy.log"
  exit 0
fi

if [ "$install" = 1 ]; then
  # The uploaded files sit in <deploy dir>/.incoming, next to the files they replace.
  [ "$(basename "$script_dir")" = .incoming ] || { echo "--install must run .incoming/deploy.sh" >&2; exit 2; }
  incoming="$script_dir"
  cd "$incoming/.."
else
  cd "$script_dir"
fi

# 1. One deploy at a time. The lock is released when this process exits.
exec 9> "$LOCK_FILE"
flock -n 9 || refuse "another deploy is running (it holds $PWD/$LOCK_FILE)"

[ -f "$ENV_FILE" ] || refuse "$PWD/$ENV_FILE is missing (see docs/deployment.md)"

compose() { docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"; }
# The compose file for steps 2 and 3: the uploaded one with --install (not in place yet), otherwise the live one.
target_compose() {
  if [ "$install" = 1 ]; then
    docker compose -f "$incoming/$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"
  else
    compose "$@"
  fi
}

previous=""
if [ -s "$LAST_GOOD" ]; then previous="$(tr -d '[:space:]' < "$LAST_GOOD")"; fi
log "deploying $tag (last good: ${previous:-none})"
export TICKPULSE_TAG="$tag"

# 2. Validate before anything changes.
if [ "$install" = 1 ]; then
  for f in "$COMPOSE_FILE" "${SCRIPTS[@]}" observability; do
    [ -e "$incoming/$f" ] || refuse "$incoming/$f is missing from the upload"
  done
  for f in "${SCRIPTS[@]}"; do
    bash -n "$incoming/$f" || refuse "$incoming/$f has a syntax error"
  done
else
  [ -f "$COMPOSE_FILE" ] || refuse "$PWD/$COMPOSE_FILE is missing"
fi
target_compose config --quiet || refuse "the compose file does not validate with .env"
log "the compose file validates"

# 3. Pull with the same compose file.
if [ "${SKIP_PULL:-0}" != 1 ]; then
  target_compose pull --quiet || { log "pull failed; nothing was changed"; exit 1; }
  log "pulled the images for $tag"
fi

installed=0

# 4. Put the uploaded files in place; the live ones go to .previous/.
install_upload() {
  local f
  rm -rf "$PREVIOUS"
  mkdir "$PREVIOUS"
  for f in "$COMPOSE_FILE" "${SCRIPTS[@]}"; do
    if [ -e "$f" ]; then mv "$f" "$PREVIOUS/"; fi
  done
  if [ ! -d observability ]; then
    mv "$incoming/observability" observability
  elif diff -rq observability "$incoming/observability" > /dev/null; then
    # Unchanged: the live directory stays (its containers keep running); .previous gets a copy.
    cp -a observability "$PREVIOUS/observability"
    rm -rf "$incoming/observability"
  else
    log "observability/ changed; its containers will be recreated"
    # Renaming keeps the inodes the running containers have mounted, so they read the old config until
    # step 5 recreates them on the new directory.
    mv observability "$PREVIOUS/observability"
    mv "$incoming/observability" observability
    touch "$RECREATE_MARKER"
  fi
  # mv keeps the inode, so this running script is not disturbed by moving it.
  mv -f "$incoming/$COMPOSE_FILE" "$incoming/smoke.sh" "$incoming/deploy.sh" .
  chmod 755 "${SCRIPTS[@]}"
  installed=1
  log "installed the uploaded files; the previous ones are in $PREVIOUS/"
}

# Rollback of step 4: the files of the failed deploy go to .failed-<time>/ and those in .previous/ come back.
# Like install_upload, it renames rather than deletes, because containers may still read the failed config.
restore_previous() {
  local f failed
  [ -f "$PREVIOUS/$COMPOSE_FILE" ] || { log "no previous files to restore"; return 0; }
  failed=".failed-$(date -u +%Y%m%dT%H%M%S)"
  mkdir "$failed"
  for f in "$COMPOSE_FILE" "${SCRIPTS[@]}"; do
    if [ -e "$PREVIOUS/$f" ]; then
      if [ -e "$f" ]; then mv "$f" "$failed/"; fi
      mv "$PREVIOUS/$f" .
    fi
  done
  if [ -d "$PREVIOUS/observability" ] && ! diff -rq observability "$PREVIOUS/observability" > /dev/null; then
    mv observability "$failed/observability"
    mv "$PREVIOUS/observability" observability
    touch "$RECREATE_MARKER"
  fi
  log "restored the previous compose file, scripts and observability/ (the failed ones are in $failed/)"
}

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

# 5-7 for <tag>, with the files in place. Returns non-zero at the first step that fails.
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

# Delete the other local tags of the api image (that repository only, nothing else on the host), so the server
# keeps the running tag and the rollback target.
prune_api_images() {
  local t k keep
  docker image ls "$IMAGE" --format '{{.Tag}}' | while read -r t; do
    keep=0
    for k in "$@" latest '<none>'; do [ "$t" = "$k" ] && keep=1; done
    [ "$keep" = 1 ] || docker image rm "$IMAGE:$t" > /dev/null 2>&1 || true
  done
}

if [ "$install" = 1 ]; then install_upload; fi

if start "$tag"; then
  printf '%s\n' "$tag" > "$LAST_GOOD"
  # Every container now runs on the live files, so the leftovers of earlier failed deploys can go.
  rm -rf .failed-*
  prune_api_images "$tag" "$previous"
  log "deployed $tag"
  exit 0
fi

log "tag $tag failed; last api log lines:"
compose logs --no-color --tail 50 api || true

if [ -z "$previous" ] || [ "$previous" = "$tag" ]; then
  log "no earlier good tag to roll back to"
  exit 1
fi

if [ "$installed" = 1 ]; then restore_previous; fi
log "rolling back to $previous"
if start "$previous"; then
  log "rolled back to $previous; $tag is NOT deployed"
  exit 1
fi

log "rollback to $previous failed as well; the site may be down"
exit 3
