"""Offline packaging and subprocess protocol checks; never contact a GeoIP provider.

Set IQM_GEO_HELPER to a built Linux helper to include executable checks. Source
checks alone are not evidence of a Docker build or successful provider access.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import unittest
import zipfile
import shutil

ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / 'tools/NextTraceGeoHelper'


class GeoHelperPackaging(unittest.TestCase):
    def test_pinned_offline_build_and_runtime(self):
        docker = (ROOT / 'deploy/Dockerfile').read_text()
        runtime = docker.split(' AS runtime', 1)[1]
        self.assertIn('go1.26.5.linux-amd64.tar.gz', docker)
        self.assertIn('5c2c3b16caefa1d968a94c1daca04a7ca301a496d9b086e17ad77bb81393f053', docker)
        self.assertIn('RUN --network=none sh tools/NextTraceGeoHelper/build-linux.sh', docker)
        self.assertIn('USER 10001:10001', runtime)
        self.assertIn('ca-certificates', runtime)
        self.assertIn('IPQUALITY_NEXTTRACE_PATH=/usr/local/libexec/iqm-nexttrace-geo', runtime)
        self.assertIn('test -z "$(getcap /usr/local/libexec/iqm-nexttrace-geo)"', runtime)
        self.assertNotIn('setcap cap_net_raw=ep /usr/local/libexec/iqm-nexttrace-geo', runtime)
        self.assertNotIn('/usr/local/go', runtime)
        self.assertNotIn('EXPOSE 443', runtime)
        build = (HELPER / 'build-linux.sh').read_text()
        for requirement in ('GOTOOLCHAIN=local', 'CGO_ENABLED=0', 'GOOS=linux', 'GOARCH=amd64', 'GOPROXY="file://', "GOVCS='*:off'", 'nexttrace-source.tar.gz', 'nexttrace-build-info.txt'):
            self.assertIn(requirement, build)
        self.assertIn('!tools/NextTraceGeoHelper/dependency-source/**/*.zip', (ROOT / 'deploy/Dockerfile.dockerignore').read_text())

    def test_module_source_checksums(self):
        folder = HELPER / 'dependency-source'
        entries = (folder / 'SHA256SUMS.txt').read_text().splitlines()
        self.assertGreaterEqual(len(entries), 25)
        for entry in entries:
            digest, relative = entry.split('  ', 1)
            self.assertEqual(digest, hashlib.sha256((folder / relative).read_bytes()).hexdigest(), relative)

    def test_patch_applies_once_and_preserves_originals(self):
        spec = importlib.util.spec_from_file_location('nexttrace_patch', HELPER / 'patch_linux.py')
        patch = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(patch)
        with tempfile.TemporaryDirectory() as temporary:
            copy = Path(temporary)
            shutil.copytree(HELPER / 'upstream', copy / 'upstream')
            with zipfile.ZipFile(HELPER / 'dependency-source/github.com/tsosunchia/powclient/@v/v0.3.0.zip') as archive:
                for info in archive.infolist():
                    if info.is_dir():
                        continue
                    relative = Path(info.filename).relative_to('github.com/tsosunchia/powclient@v0.3.0')
                    target = copy / 'patched-powclient' / relative
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(archive.read(info))
            patch.patch(copy)
            hardened = (copy / 'patched-powclient/pow_client.go').read_text()
            self.assertIn('CheckRedirect:', hardened)
            self.assertIn('RetryAfterHeader()', hardened)
            self.assertIn('io.LimitReader(resp.Body, 49153)', hardened)
            self.assertNotIn('Body: bodySnippet(', hardened)
            with self.assertRaises(SystemExit):
                patch.patch(copy)


@unittest.skipUnless(os.environ.get('IQM_GEO_HELPER'), 'Set IQM_GEO_HELPER for built executable checks')
class GeoHelperProcess(unittest.TestCase):
    def launch(self, *args):
        env = {k: v for k, v in os.environ.items() if not k.upper().startswith('NEXTTRACE_')}
        return subprocess.Popen([os.environ['IQM_GEO_HELPER'], *args], stdin=subprocess.PIPE,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=env)

    def test_private_requests_and_shutdown(self):
        process = self.launch('--live', '--lifetime=5s')
        output, error = process.communicate('\n'.join([
            json.dumps({'id': 'private-v3', 'op': 'lookup', 'ips': ['127.0.0.1', '10.0.0.1', '2001:db8::1']}),
            json.dumps({'id': 'private-v4', 'op': 'v4', 'ips': ['127.0.0.1'], 'token': 'fixture'}),
            json.dumps({'id': 'stats', 'op': 'stats'}),
            json.dumps({'id': 'stop', 'op': 'shutdown'}), '']), timeout=3)
        self.assertEqual(process.returncode, 0, error)
        frames = [json.loads(line) for line in output.splitlines()]
        self.assertTrue(all(frame['schema'] == 1 for frame in frames))
        stats = next(frame for frame in frames if frame['type'] == 'stats')
        self.assertEqual((stats['queries'], stats['connections']), (0, 0))
        self.assertEqual(frames[-1]['status'], 'shutdown')
        self.assertNotIn('fixture', output + error)

    def test_malformed_and_oversized_input_exits_cleanly(self):
        process = self.launch()
        output, error = process.communicate('not-json\n' + 'x' * 65537 + '\n', timeout=3)
        self.assertEqual(process.returncode, 2)
        self.assertTrue(any(json.loads(line).get('code') == 'invalid_json' for line in output.splitlines()))
        self.assertNotIn('x' * 100, error)

    def test_hard_lifetime_and_crash_are_reaped(self):
        process = self.launch('--lifetime=100ms')
        try:
            process.wait(timeout=3)
            self.assertEqual(process.returncode, 124)
        finally:
            process.communicate(timeout=2)
        crashed = self.launch()
        self.assertEqual(json.loads(crashed.stdout.readline())['type'], 'ready')
        crashed.send_signal(signal.SIGKILL)
        crashed.communicate(timeout=3)
        self.assertEqual(crashed.returncode, -signal.SIGKILL)
        with self.assertRaises(ProcessLookupError):
            os.kill(crashed.pid, 0)


if __name__ == '__main__':
    unittest.main()
