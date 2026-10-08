# Docker branch validation status — 2026-10-08

Target: **linux/amd64 only**. Branch: `docker/bridge-preview-20261008`.
Base Windows/Core commit: `7ff18b1dc81320faea5ae5b6863146afdc8264f4`.
Core and Windows source files remain unchanged. The root README adds a Docker entry.
No PR, main merge, image registry publication, or deployment is part of this work.

## Verified baseline Docker run

Commit `9a3227bf1c6846c898b49fff0224d096723ecb7e` passed
[GitHub Actions run 37717520989](https://github.com/fengexian-c/IpQualityMonitor/actions/runs/37717520989):

- .NET 10 Release publish and server adapter console tests
- Core route probe checks (28) and route-history checks (69)
- Full pinned mtr source checkout, hash-checked patch, native build, runtime image build
- Non-root Docker bridge container with `ports`, read-only root, persistent data and NET_RAW helper
- Live HTTP login/authentication, CSRF rejection, continuous TCP and raw-ICMP loopback success
- Graceful stop, restart and stable dataset identity

That initial historical run also included arm64. The requested compatibility target is now amd64;
arm64 is no longer maintained or included in the branch CI matrix.

## Hardened branch validation

The workflow attached to each commit is the authoritative result for that exact source revision.
This revision adds API invalid-input, restart/history, file-capability and native packet regression gates,
plus 12 mocked browser interaction regressions and one live ASP.NET browser login/logout test.
The hardened commit `bffd08e` passed the Docker build, 91 server checks, native decoder and live
HTTP/ICMP/TCP/persistence gates in [run 37718356873](https://github.com/fengexian-c/IpQualityMonitor/actions/runs/37718356873).
That run caught a deferred settings-toggle overwrite in browser QA; the edit-preservation guard and
stronger regression were added afterward. Its failed browser gate is not claimed as a full pass.
The corrected browser suite (12 mocked flows + real ASP.NET auth) then passed 13/13 on `5a884c2`
in [run 37718861930](https://github.com/fengexian-c/IpQualityMonitor/actions/runs/37718861930).
That run exposed a smoke-script assumption: a dynamically published host port was reused after
Docker restart. The script now re-reads the actual published port before resume checks; the container
itself had restarted cleanly and its internal readiness endpoint returned 200.
Final outcomes for the corrected source are recorded in the
branch's [Actions history](https://github.com/fengexian-c/IpQualityMonitor/actions/workflows/server-preview.yml).

Local verification of the hardened source passed:

- Web Release publish with .NET 10.0.401, zero warnings/errors
- 91 server checks; 28 Core route checks; 69 Core history checks; 31 Core multi-target checks
- 18 Python source contracts, JavaScript/Python/shell syntax checks
- Actual localhost HTTP TCP-only smoke, nonempty stored timelines, invalid/stale input rejection,
  login/CSRF/logout, SIGTERM/restart/resume, stable identity and standalone readiness healthcheck
- Native patched-helper compile/link plus synthetic packet decoder tests; AddressSanitizer/UBSan passed

Local Chromium could not launch due to sandbox socket restrictions, so actual browser execution is
performed in CI. The workflow must pass its browser gate before the revision is considered validated. LeakSanitizer was not available under local executor tracing and was not claimed.
The local environment has no Docker daemon; Docker results come from GitHub-hosted Ubuntu amd64.
Native synthetic mtr tests compile the actual patched decoder and inject packets without live topology.
They cover handled IPv4/IPv6 reply/error classes, late replies, malformed/foreign replies and cancellation;
they do not prove behavior across a real routed network.

## Environment-bootstrap authentication update

This update adds first-initialization `IPQUALITY_ADMIN_USERNAME` and
`IPQUALITY_ADMIN_PASSWORD`, retaining the mutually exclusive password-file alternative.
Existing valid records take precedence over all bootstrap settings. Schema 1 retains its
file bytes, password and old cookie stamp with username `admin`; new records use schema 2.
Password-only API login is intentionally no longer accepted.

Local verification for this update:

- Release server build: zero warnings/errors; 213 server checks passed
- Core regressions: 28 route checks, 69 route-history checks, 31 multi-target checks
- Actual legacy published binary → updated binary HTTP upgrade preserved the schema-1
  cookie, original credential-file bytes, and login with admin plus the original password
- Username/password/source boundaries, exact env versus file CR/LF handling, persisted-setting
  precedence, schema compatibility, stamp rotation, malformed records and storage lock covered
- Fresh named-volume ownership, lifecycle, HTTP and real-browser tests are CI gates;
  local Docker and Chromium execution are unavailable in this executor

See the exact commit's Actions result for the final container/browser outcome. Tests use
only disposable credentials and data. No image is published by this workflow.

## Scope and remaining acceptance work

- Only linux/amd64 is supported by this branch's deployment settings and ongoing CI
- Loopback packet tests do not establish Internet connectivity, real TTL/unreachable paths,
  packet loss accuracy across real routers, public IPv6, or a particular NAS's file-capability behavior
- No 24–72 hour load/soak run, full backup/restore exercise or upgrade acceptance has been completed
- Existing Windows-specific tests and UI were not run on Windows; original Windows code is unchanged
- Full Windows feature parity is not claimed: NextTrace/online annotations, complete route-comparison UI,
  Windows archive import, online-consistent backup UI and target editing remain future work
- Default history retention remains Core's 31 days; bridge+ports remains mandatory
- Default listen address is host loopback; an explicit initial password is required; use a trusted HTTPS reverse
  proxy or private LAN access as documented, never expose plain HTTP credentials on the public Internet

## Reproducible commands

```sh
python3 -m unittest discover -s tests/server -p 'test_*.py' -v
node --check src/IpQualityMonitor.Web/wwwroot/app.js
bash -n tests/server/smoke-container.sh
sh -n deploy/init.sh
docker build --target tests -f deploy/Dockerfile .
docker build -t ipqualitymonitor:server-preview -f deploy/Dockerfile .
bash tests/server/smoke-container.sh
```

The smoke script owns only a fresh disposable dataset and loopback target. It never reuses production data.
The build uses pinned base manifest digests and mtr revision; package security updates require deliberate
rebuild/retest. Registry apt packages are resolved at build time, so this is not a fully bit-reproducible image.


## NextTrace annotations implementation (2026-10-08)

Prepared on base `702377e09cc562461deb7f4c61d7c2cb158936cf`:
- Offline by default; mtr measurement and raw route JSON unchanged
- Read-only annotation API, separate measured/annotated/queried timestamps, per-IP provenance and stale/attempt states
- Bounded 128-job queue, coalescing and recovery of the newest 512 configured-target routes from 24 hours; one due public IP per server slice
- Explicit v3/v4 with no fallback; secret-file-only v4 credential; persistent auth block, quota/cooldown and fair retry order
- Optional annotation/metadata transactions isolated; real SQLite I/O failures still stop/report the raw writer
- Pinned Linux amd64 helper and source/license delivery; no new port, root mode or helper capability

Local verification: .NET server build zero warnings/errors; all 320 server checks passed, including existing environment-login suites and new storage/scheduler/provider/process/runtime checks. Existing focused Core suites passed: routes 28, route history 69, multi-target 31, NextTrace 16, geo 46, locations 36. Helper was built from the pinned local dependencies with Go 1.26.5, and mock Go/Python protocol checks passed. JavaScript syntax and static Python checks passed.

Local limitations: Docker is unavailable; Chromium cannot create its sandbox socket (EPERM). The default Windows-oriented Core run reaches an unsupported native ICMP loopback call; it is not reported as a full pass. Container/bridge/HTTP/browser checks are configured for the authorized amd64 CI and must be read on the exact pushed commit before claiming those gates passed.

No real NextTrace service query, user monitoring-target probe, real credential use, deployment, image publication or main merge was performed for this feature. Synthetic PoW cost is not a production resource guarantee. See `docs/NEXTTRACE-DOCKER.md` for privacy, service-permission and validation boundaries.
