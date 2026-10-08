# Docker NextTrace GeoIP helper (linux/amd64)

The Docker server includes `/usr/local/libexec/iqm-nexttrace-geo`. It enriches
public route-hop IPs only; mtr continues to own route probing. The helper has no
listener, root requirement, setuid bit or file capability. It runs as the same
UID/GID `10001:10001` as the application. The existing mtr capability is unchanged.

## Provider selection and credentials

- The settings modes are exactly `offline` (default), `v3`, and `v4`. A failure
  never silently switches providers, versions, credentials or authority.
- v3 uses the pinned NextTrace PoW/WebSocket implementation. PoW consumes CPU;
  the helper limits CPU parallelism to one, bounds authentication and lookups,
  and stops a failed connection epoch. An individual operation is not retried
  internally. The application owns cache, cooldown and any later user-requested
  retry.
- v4 uses `https://api.nxtrace.org/v4/ipGeo`, with the credential in the
  `X-NextTrace-Token` header. The credential reaches the helper only through its
  private stdin. It is not a command-line argument, image layer or build input.
- Both paths verify TLS certificates and hostnames. HTTP redirects are refused.
  401/403, 429 and other HTTP status metadata are returned without provider error
  bodies. `Retry-After` preserves seconds or HTTP-date form for the application.
- Private, loopback, link-local, multicast, reserved and documentation addresses
  are rejected before any GeoIP connection. Public IPv4-mapped IPv6 is normalized.
- The stdin/stdout protocol uses schema 1, exact request IDs and normalized IPs.
  Requests are limited to 64 KiB and 32 addresses; provider GeoIP responses are
  bounded at 48 KiB. A timeout ends its v3 epoch so a late reply cannot be reused
  for a later request.

The executable path may be set with `IPQUALITY_NEXTTRACE_PATH`; the Docker image
already sets the installed path. There is no additional Compose port,
root mode, host network or raw-socket permission for GeoIP. Existing persistent
annotation/cache state stays in the application's data volume. The v4 token is
external: `IPQUALITY_NEXTTRACE_TOKEN_FILE` names a read-only mounted file readable
by UID 10001. Never put its contents in configuration JSON, the data database,
Compose environment values, logs, screenshots, image layers or source control.

A Compose override can supply the file without putting its value in YAML:

```yaml
services:
  ipqualitymonitor:
    environment:
      IPQUALITY_NEXTTRACE_TOKEN_FILE: /run/secrets/nexttrace_token
    secrets:
      - nexttrace_token
secrets:
  nexttrace_token:
    file: ./secrets/nexttrace_token.txt
```

The operator creates that secret file outside source control, makes it readable
by the container's UID/GID 10001, and keeps it read-only in the container. Confirm
the mounted file's actual owner/mode; local Compose secrets may retain the host
file's permissions. Select `v4` only after mounting a valid token. `offline` and
`v3` do not need a v4 token. Missing/unreadable/rejected credentials remain visible
failures; they do not switch the server to v3.

## Privacy and annotation history

Enabling v3 or v4 sends individual public hop IPs to NextTrace, and the provider
can see the server's outbound/egress IP. It does not send target names, a complete
route, private/reserved hop IPs or the application's history database. Enrichment
results can be approximate or stale; route measurement and location evidence are
separate records.

Measured at is the raw route observation time. First annotated at is the
immutable first saved interpretation of that route; latest annotated at is the
most recently saved interpretation. Queried at belongs to each item of location
evidence; reading cached data does not move that time. Re-annotation can update
the latest interpretation without overwriting the first one or the raw route. After restart, recovery considers at most 512 latest snapshots from the
last 24 hours and only currently configured targets. Raw history retention still
applies, so deleted/expired route observations cannot be reconstructed from a
GeoIP cache. Cached evidence and current query failure/stale status remain
visible rather than being replaced by a false success.

## Reproducible build and source delivery

The helper stays on NTrace-core **v1.7.3**, commit
`40ac803ca233b0bc3d4b3bf1cacc8f7e0d8368f2`. All original upstream files are retained.
The Linux-only `patch_linux.py` verifies exact file hashes and patches temporary
build copies. It adds:

1. No redirects for PoW and endpoint latency HTTP requests
2. Bounded challenge/response/WebSocket sizes and linked cancellation
3. One PoW attempt and no automatic reconnect after a recorded failure
4. Safe HTTP status and Retry-After transport for PoW/WebSocket failures

No dependency is upgraded. The Linux build includes the exact gocapability
version already required by pinned upstream; its code does not grant privileges.
Dependency source ZIPs and SHA-256 checksums are delivered locally. After the
compiler is installed, `RUN --network=none` enforces offline build and mock tests.

Toolchain: **Go 1.26.5**, `CGO_ENABLED=0 GOOS=linux GOARCH=amd64 GOAMD64=v1`,
`GOTOOLCHAIN=local`. The official compiler archive SHA-256 is
`5c2c3b16caefa1d968a94c1daca04a7ca301a496d9b086e17ad77bb81393f053`, verified against
[Go's official download metadata](https://go.dev/dl/?mode=json&include=all).
Go and its build cache are absent from the runtime image; system CA certificates
are included. The executable is statically linked.

The image carries these files under `/usr/share/doc/ipqualitymonitor/`:

- `nexttrace-LICENSE`, `nexttrace-Go-LICENSE` and `nexttrace-NOTICE`
- `nexttrace-source.tar.gz`, containing complete corresponding helper/upstream/
  dependency source, licenses, hardening patch and build scripts
- `nexttrace-provenance.json` and `nexttrace-build-info.txt`
- `nexttrace-mock-pow-benchmark.txt` for the synthetic test used during the build

To rebuild with the verified compiler:

```sh
GO=/path/to/go1.26.5/bin/go sh tools/NextTraceGeoHelper/build-linux.sh /tmp/geo-artifacts
IQM_GEO_HELPER=/tmp/geo-artifacts/iqm-nexttrace-geo \
  python3 -m unittest discover -s tests/server -p test_geo_helper_contracts.py -v
docker build --platform linux/amd64 -f deploy/Dockerfile -t iqm:nexttrace .
```

## What verification means

Tests use injected transports or loopback fake providers, synthetic credentials
and public-format fixture IPs. They check v4 statuses/redirects, patched v3 PoW
statuses/Retry-After, cancellation, malformed/oversized payloads, private-address
zero-outbound behavior, process lifetime, crash reaping and protocol correlation.
They never use real provider tokens, real route targets or live GeoIP queries.

The PoW benchmark factors the trivial synthetic challenge `35` against a local
fake server. Its latency and allocations measure test overhead only. They do not
predict real NextTrace PoW difficulty, provider availability, production memory
use or sustainable request rate. Image smoke tests must still verify UID 10001,
the installed helper, empty helper capabilities and CA availability in the exact
published image. Passing source/protocol tests alone is not that evidence.

GPL source availability grants no right to use the online APIs; provider service
authorization, quotas and terms still apply.
