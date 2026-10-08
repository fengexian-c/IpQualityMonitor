#!/usr/bin/env bash
set -euo pipefail
root=$(mktemp -d)
name="iqm-smoke-$RANDOM"
cleanup() { docker logs "$name" 2>&1 || true; docker rm -f "$name" >/dev/null 2>&1 || true; sudo rm -rf "$root"; }
trap cleanup EXIT
mkdir -p "$root/data"
printf '%s\n' 'smoke-test-only-not-a-production-password' > "$root/password"
sudo chown 10001:10001 "$root/data" "$root/password"
sudo chmod 0700 "$root/data"
sudo chmod 0400 "$root/password"
docker run -d --name "$name" --network bridge --init \
  --cap-drop ALL --cap-add NET_RAW --read-only --tmpfs /tmp:rw,nosuid,nodev,size=64m \
  -p 127.0.0.1::8080 \
  -v "$root/data:/data" -v "$root/password:/run/secrets/admin_password:ro" \
  -e IPQUALITY_ADMIN_PASSWORD_FILE=/run/secrets/admin_password \
  ipqualitymonitor:server-preview
port=$(docker port "$name" 8080/tcp | head -1 | sed 's/.*://')
sudo python3 tests/server/smoke_http.py --url "http://127.0.0.1:$port" --password-file "$root/password"
# SIGTERM should retain desired running state and the same stable instance manifest.
before=$(sudo cat "$root/data/instance.json")
docker stop --time 45 "$name" >/dev/null
test "$(docker inspect -f '{{.State.ExitCode}}' "$name")" = 0
docker start "$name" >/dev/null
sleep 3
after=$(sudo cat "$root/data/instance.json")
test "$before" = "$after"
printf '%s\n' 'PASS stable dataset identity after restart'
