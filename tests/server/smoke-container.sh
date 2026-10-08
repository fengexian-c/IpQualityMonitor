#!/usr/bin/env bash
set -euo pipefail
root=$(mktemp -d)
name="iqm-smoke-$RANDOM"
browser_password=""
cleanup() { if [ -n "$browser_password" ]; then rm -f "$browser_password"; fi; docker logs "$name" 2>&1 || true; docker rm -f "$name" >/dev/null 2>&1 || true; sudo rm -rf "$root"; }
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
test "$(docker exec "$name" id -u)" = 10001
# The web process has no effective capabilities; only the packet helper receives NET_RAW.
docker exec "$name" sh -c "grep '^CapEff:[[:space:]]*0000000000000000$' /proc/1/status"
docker exec "$name" getcap /usr/local/libexec/iqm-mtr-packet | grep -q cap_net_raw
test -z "$(docker exec "$name" getcap /usr/local/libexec/iqm-nexttrace-geo)"
# Run the actual static helper as UID 10001; only reserved/private inputs, no provider connection.
printf '%s\n' '{"id":"private","op":"lookup","ips":["127.0.0.1","10.0.0.1","2001:db8::1"]}' '{"id":"stats","op":"stats"}' '{"id":"stop","op":"shutdown"}' | \
  docker exec -i "$name" /usr/local/libexec/iqm-nexttrace-geo --live --lifetime=5s | \
  python3 -c 'import json,sys; frames=[json.loads(s) for s in sys.stdin]; stats=next(f for f in frames if f["type"]=="stats"); assert stats["queries"]==0 and stats["connections"]==0; assert frames[-1]["status"]=="shutdown"'
port=$(docker port "$name" 8080/tcp | head -1 | sed 's/.*://')
printf 'Checking published loopback port %s\n' "$port"
sudo python3 tests/server/smoke_http.py --url "http://127.0.0.1:$port" --password-file "$root/password"
docker exec "$name" dotnet /app/IpQualityMonitor.Web.dll --healthcheck
if [ "${IQM_BROWSER_SMOKE:-0}" = 1 ]; then
  browser_password=$(mktemp)
  sudo cat "$root/password" > "$browser_password"
  node tests/server/browser-smoke.cjs --url "http://127.0.0.1:$port" --password-file "$browser_password"
  rm -f "$browser_password"
  browser_password=""
fi
# SIGTERM should retain desired running state and the same stable instance manifest.
before=$(sudo cat "$root/data/instance.json")
docker stop --time 45 "$name" >/dev/null
test "$(docker inspect -f '{{.State.ExitCode}}' "$name")" = 0
# Docker may allocate a different ephemeral host port when the container restarts.
docker start "$name" >/dev/null
port=$(docker port "$name" 8080/tcp | head -1 | sed 's/.*://')
printf 'Checking published loopback port %s\n' "$port"
sudo python3 tests/server/smoke_http.py --url "http://127.0.0.1:$port" --password-file "$root/password" --resume
after=$(sudo cat "$root/data/instance.json")
test "$before" = "$after"
printf '%s\n' 'PASS stable dataset identity after restart'

# Real container lifecycle cases use only freshly created labeled named volumes.
python3 tests/server/auth_lifecycle.py

