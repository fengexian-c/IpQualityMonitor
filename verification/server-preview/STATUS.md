# Server preview validation status — 2026-09-19

Base repository commit: `7ff18b1dc81320faea5ae5b6863146afdc8264f4`.
This delivery adds server projects/files; it does not modify the existing Core or Windows App.

## Executed in the working environment

- 15 offline Python source-contract checks passed. See `offline-checks.txt`.
- The exact monotonic-clock header and millisecond-expiry C fragments carried by the mtr patch were
  compiled with GCC (`-std=c11 -D_POSIX_C_SOURCE=200809L -Wall -Wextra -Werror`) and executed.
  This checks those fragments, **not the entire patched mtr build**.
- JavaScript syntax check passed with `node --check`.
- Shell syntax checks passed for `deploy/init.sh` and the container smoke script.
- Python files compiled; JSON/YAML/project XML syntax was checked.
- Chromium offline mocked-fetch UI test passed: login flow, overview, timeline SVG, hour buckets,
  raw route table, settings values, 390px mobile width, no JS page errors.
  The test replaced fetch and browser storage with local mock data; it was **not** an HTTP,
  ASP.NET, database, authentication-cryptography or Docker integration test.
- The generated additive Git patch passed `git apply --check` in a clean temporary repository.

## Not executed; required before treating this as a usable release

- .NET compilation and the included .NET console tests (SDK not available in this environment).
- The full mtr upstream checkout, patch application and native build.
- Docker image build, Compose validation by Docker itself and the included live smoke tests.
- Final-image non-root NET_RAW/file-capability checks.
- Real IPv4/IPv6 TTL/unreachable/late-packet/cancellation tests.
- Windows regression runs, ARM64 runs, 24–72 hour load/soak, restore and upgrade acceptance.
- NextTrace, full route-comparison Web UI and Windows archive import are not part of this delivery.

Do not interpret source checks or mocked UI screenshots as evidence that the image already builds/runs.

## Remote repository

GitHub branch creation returned HTTP 403 (`Resource not accessible by integration`).
No branch, commit, pull request, workflow run, image or release was created remotely.
The branch name used in instructions is a proposed local development branch, not an existing remote branch.
