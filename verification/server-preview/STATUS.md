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
This revision adds API invalid-input, restart/history, file-capability and native packet regression gates.
Do not infer their result from the older baseline run above. Final outcomes are recorded in the
branch's [Actions history](https://github.com/fengexian-c/IpQualityMonitor/actions/workflows/server-preview.yml).

Local verification of the hardened source passed:

- Web Release publish with .NET 10.0.401, zero warnings/errors
- 91 server checks; 28 Core route checks; 69 Core history checks; 31 Core multi-target checks
- 18 Python source contracts, JavaScript/Python/shell syntax checks
- Actual localhost HTTP TCP-only smoke, nonempty stored timelines, invalid/stale input rejection,
  login/CSRF/logout, SIGTERM/restart/resume, stable identity and standalone readiness healthcheck
- Native patched-helper compile/link plus synthetic packet decoder tests; AddressSanitizer/UBSan passed

Local Chromium could not launch due to sandbox socket restrictions, so actual browser execution is
assigned to CI. LeakSanitizer was not available under local executor tracing and was not claimed.
The local environment has no Docker daemon; Docker results come from GitHub-hosted Ubuntu amd64.
Native synthetic mtr tests compile the actual patched decoder and inject packets without live topology.
They cover handled IPv4/IPv6 reply/error classes, late replies, malformed/foreign replies and cancellation;
they do not prove behavior across a real routed network.

## Scope and remaining acceptance work

- Only linux/amd64 is supported by this branch's deployment settings and ongoing CI
- Loopback packet tests do not establish Internet connectivity, real TTL/unreachable paths,
  packet loss accuracy across real routers, public IPv6, or a particular NAS's file-capability behavior
- No 24–72 hour load/soak run, full backup/restore exercise or upgrade acceptance has been completed
- Existing Windows-specific tests and UI were not run on Windows; original Windows code is unchanged
- Full Windows feature parity is not claimed: NextTrace/online annotations, complete route-comparison UI,
  Windows archive import, online-consistent backup UI and target editing remain future work
- Default history retention remains Core's 31 days; bridge+ports remains mandatory
- Default listen address is host loopback; a generated password is required; use a trusted HTTPS reverse
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
