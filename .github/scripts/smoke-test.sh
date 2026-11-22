#!/usr/bin/env bash
# Start a fresh Jellyfin with only the given plugin zip installed and fail
# unless it becomes healthy and reports the plugin as loaded.
#
# Usage: smoke-test.sh <plugin.zip> <jellyfin image tag>
set -euo pipefail
zip=$1
image="jellyfin/jellyfin:$2"
work=$(mktemp -d)
name="shokofin-smoke-$$"
mkdir -p "$work/config/plugins/Shoko_smoke" "$work/cache"
unzip -q "$zip" -d "$work/config/plugins/Shoko_smoke"
chmod -R a+rwX "$work"
trap 'docker logs "$name" > "$work/jellyfin.log" 2>&1 || true; docker rm -f "$name" >/dev/null 2>&1 || true' EXIT

docker run -d --name "$name" -v "$work/config:/config" -v "$work/cache:/cache" "$image" >/dev/null
result=timeout
for _ in $(seq 1 90); do
  sleep 2
  if [ "$(docker inspect -f '{{.State.Status}}' "$name")" != running ]; then
    result="exited with code $(docker inspect -f '{{.State.ExitCode}}' "$name")"
    break
  fi
  if [ "$(docker exec "$name" curl -s -m 2 http://localhost:8096/health 2>/dev/null || true)" = Healthy ]; then
    sleep 5
    result=healthy
    break
  fi
done

logs=$(docker logs "$name" 2>&1 || true)
if [ "$result" = healthy ] && grep -q 'Loaded plugin: Shoko ' <<<"$logs" && ! grep -qE '\[FTL\]' <<<"$logs"; then
  echo "Jellyfin ($image) started with $(grep -o 'Loaded plugin: Shoko [0-9.]*' <<<"$logs")"
  exit 0
fi

echo "::error::Jellyfin ($image) did not start cleanly with $(basename "$zip"): $result"
grep -nE '\[(FTL|ERR)\]|Exception|Shoko' <<<"$logs" | head -60 || true
exit 1
