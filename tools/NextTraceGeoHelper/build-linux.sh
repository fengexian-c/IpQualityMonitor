#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-only
# Build only from the checked-in sources and module proxy; no dependency network.
set -eu
src=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
go=${GO:-go}
out=${1:-/tmp/nexttrace-artifacts}
mkdir -p "$out"
out=$(CDPATH= cd -- "$out" && pwd)
test "$("$go" version)" = 'go version go1.26.5 linux/amd64'
export GOTOOLCHAIN=local CGO_ENABLED=0 GOOS=linux GOARCH=amd64 GOAMD64=v1
export GOPROXY="file://$src/dependency-source" GOSUMDB=off GONOSUMDB='*' GOVCS='*:off'
export GOPATH=${GOPATH:-/tmp/nexttrace-gopath} GOCACHE=${GOCACHE:-/tmp/nexttrace-gocache}
build=$(mktemp -d)
trap 'rm -rf "$build"' EXIT HUP INT TERM
(cd "$src/dependency-source" && sha256sum -c SHA256SUMS.txt)
cp -a "$src/." "$build/"
python3 - "$build" <<'PY'
import pathlib, sys, zipfile
root = pathlib.Path(sys.argv[1])
with zipfile.ZipFile(root/'dependency-source/github.com/tsosunchia/powclient/@v/v0.3.0.zip') as archive:
    for info in archive.infolist():
        relative = pathlib.PurePosixPath(info.filename).relative_to('github.com/tsosunchia/powclient@v0.3.0')
        if '..' in relative.parts or info.is_dir():
            continue
        target = root/'patched-powclient'/relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(archive.read(info))
PY
python3 "$src/patch_linux.py" "$build"
cd "$build"
"$go" mod edit -replace=github.com/tsosunchia/powclient=./patched-powclient
# Linux hardening tests are included only with the patched build.
cp linux-tests/linux_hardening_test.go.in ./linux_hardening_test.go
cp linux-tests/wshandle/iqm_test.go.in upstream/NTrace-core-40ac803ca233b0bc3d4b3bf1cacc8f7e0d8368f2/wshandle/iqm_test.go
"$go" test -mod=readonly -count=1 -timeout 45s github.com/nxtrace/NTrace-core/wshandle -run '^TestIQM'
"$go" test -mod=readonly -count=1 -timeout 45s .
GOMAXPROCS=1 "$go" test -mod=readonly -run '^$' -bench '^BenchmarkLinuxMockPoW$' -benchtime=20x -count=1 . > "$out/nexttrace-mock-pow-benchmark.txt"
cat "$out/nexttrace-mock-pow-benchmark.txt"
"$go" build -mod=readonly -buildvcs=false -trimpath -ldflags '-s -w' -o "$out/iqm-nexttrace-geo" .
chmod 0755 "$out/iqm-nexttrace-geo"
"$go" version -m "$out/iqm-nexttrace-geo" > "$out/nexttrace-build-info.txt"
sha256sum "$out/iqm-nexttrace-geo" >> "$out/nexttrace-build-info.txt"
printf '%s\n' 'NextTrace v1.7.3 commit 40ac803ca233b0bc3d4b3bf1cacc8f7e0d8368f2' \
    'Linux adapter hardening: patch_linux.py (original upstream retained)' \
    'Go 1.26.5 linux/amd64; CGO_ENABLED=0; GOAMD64=v1; GOTOOLCHAIN=local' >> "$out/nexttrace-build-info.txt"
cp "$src/upstream/NTrace-core-40ac803ca233b0bc3d4b3bf1cacc8f7e0d8368f2/LICENSE" "$out/nexttrace-LICENSE"
cp "$src/NOTICE" "$out/nexttrace-NOTICE"
cp "$("$go" env GOROOT)/LICENSE" "$out/nexttrace-Go-LICENSE"
cp "$src/provenance.json" "$out/nexttrace-provenance.json"
# Complete corresponding source includes patch, build recipe, original source,
# dependencies and their licenses. No downloaded toolchain or compiled cache.
tar --exclude='./.git' --exclude='./__pycache__' --exclude='*.pyc' \
    -czf "$out/nexttrace-source.tar.gz" -C "$src" .
