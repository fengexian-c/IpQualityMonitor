"""Offline checks for the delivered source. These do not replace dotnet/Docker/network tests."""
import ast
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
PATCH = ROOT / 'tools/mtr/patch.py'
spec = importlib.util.spec_from_file_location('iqm_patch', PATCH)
patch = importlib.util.module_from_spec(spec)
spec.loader.exec_module(patch)


class SourceContracts(unittest.TestCase):
    def test_compose_bridge_and_ports(self):
        text = (ROOT / 'deploy/compose.yaml').read_text()
        self.assertRegex(text, r'(?m)^    ports:\s*$')
        self.assertIn(':8080"', text)
        self.assertIn('driver: bridge', text)
        self.assertNotIn('network_mode:', text)
        self.assertNotIn('privileged:', text)
        self.assertNotIn('NET_ADMIN', text)
        self.assertNotIn('docker.sock', text)
        self.assertIn('NET_RAW', text)

    def test_supported_architecture_and_ci_scope(self):
        compose = (ROOT / 'deploy/compose.yaml').read_text()
        self.assertIn('platform: linux/amd64', compose)
        workflow = (ROOT / '.github/workflows/server-preview.yml').read_text()
        self.assertIn("'docker/**'", workflow)
        self.assertNotIn('ubuntu-24.04-arm', workflow)
        self.assertIn('contents: read', workflow)
        self.assertNotIn('docker push', workflow)
        self.assertNotIn('packages: write', workflow)

    def test_runtime_base_images_are_pinned(self):
        dockerfile = (ROOT / 'deploy/Dockerfile').read_text()
        bases = re.findall(r'^FROM (\S+)', dockerfile, re.M)
        for base in bases:
            if base != 'build':
                self.assertRegex(base, r'@sha256:[0-9a-f]{64}$')

    def test_privileged_initializer_refuses_symlinks(self):
        script = (ROOT / 'deploy/init.sh').read_text()
        self.assertLess(script.index('if [ -L "$path" ]'), script.index('chown 10001:10001 data'))
        self.assertIn('data secrets secrets/admin_password.txt .env', script)
        self.assertIn('umask 077', script)

    def test_ipv6_is_explicit_optional_override(self):
        text = (ROOT / 'deploy/compose.ipv6.yaml').read_text()
        self.assertIn('enable_ipv6: true', text)
        self.assertNotIn('host', (ROOT / 'deploy/compose.yaml').read_text().replace('host IPv4', ''))

    def test_final_file_capability(self):
        text = (ROOT / 'deploy/Dockerfile').read_text()
        copy = text.index('COPY --from=packet /artifacts/iqm-mtr-packet')
        capability = text.index('setcap cap_net_raw=ep')
        self.assertLess(copy, capability)
        self.assertIn('USER 10001:10001', text)
        self.assertIn('mtr-source.tar.gz', text)
        self.assertNotIn('no-new-privileges:', (ROOT / 'deploy/compose.yaml').read_text())

    def test_project_xml_and_dependency_direction(self):
        for file in ROOT.glob('src/IpQualityMonitor.*/*.csproj'):
            tree = ET.parse(file)
            self.assertEqual(tree.findtext('.//TargetFramework'), 'net10.0')
            for reference in tree.findall('.//ProjectReference'):
                self.assertNotIn('TcpLatencyMonitor.App', reference.attrib['Include'])
        self.assertTrue((ROOT / 'src/IpQualityMonitor.Application/MonitorRuntime.cs').is_file())

    def test_no_network_targets_seeded(self):
        text = (ROOT / 'src/IpQualityMonitor.Application/ServerStorage.cs').read_text()
        self.assertIn('ServerConfiguration.Empty', text)
        for ip in ['8.8.8.8', '1.1.1.1']:
            self.assertNotIn(ip, text)

    def test_bootstrap_sources_and_compose_secret_file(self):
        compose = (ROOT / 'deploy/compose.yaml').read_text()
        self.assertIn('IPQUALITY_ADMIN_PASSWORD_FILE:', compose)
        self.assertIn('IPQUALITY_ADMIN_USERNAME: "${IPQUALITY_ADMIN_USERNAME-admin}"', compose)
        self.assertNotIn('IPQUALITY_ADMIN_PASSWORD:', compose)
        text = (ROOT / 'src/IpQualityMonitor.Web/AdminCredentials.cs').read_text()
        self.assertIn('FixedTimeEquals', text)
        self.assertIn('Rfc2898DeriveBytes.Pbkdf2', text)
        self.assertIn('password.Length is < 16', text)
        for setting in ('IPQUALITY_ADMIN_USERNAME', 'IPQUALITY_ADMIN_PASSWORD', 'IPQUALITY_ADMIN_PASSWORD_FILE'):
            self.assertIn('configuration["' + setting + '"]', text)
        self.assertIn('StringComparison.Ordinal', text)
        self.assertNotIn('password.Trim()', text)

    def test_username_login_ui_and_required_api_fields(self):
        html = (ROOT / 'src/IpQualityMonitor.Web/wwwroot/index.html').read_text()
        username = re.search(r'<input\b[^>]*\bid="username"[^>]*>', html)
        self.assertIsNotNone(username)
        for attribute in ('required', 'autocomplete="username"', 'maxlength="64"', 'value="admin"'):
            self.assertIn(attribute, username.group())
        script = (ROOT / 'src/IpQualityMonitor.Web/wwwroot/app.js').read_text()
        self.assertIn("username: $('username').value", script)
        self.assertIn("password: $('password').value", script)
        program = (ROOT / 'src/IpQualityMonitor.Web/Program.cs').read_text()
        self.assertIn('admin.Verify(input.Username, input.Password)', program)
        self.assertRegex(program, r'LoginInput\([^;]*JsonRequired[^;]*Username[^;]*JsonRequired[^;]*Password')
        self.assertIn('ClaimTypes.Name, admin.Username', program)

    def test_auth_bootstrap_fails_before_http_listener(self):
        program = (ROOT / 'src/IpQualityMonitor.Web/Program.cs').read_text()
        startup = program.index('_ = app.Services.GetRequiredService<AdminCredentials>();')
        self.assertLess(startup, program.index('app.RunAsync()'))
        credentials = (ROOT / 'src/IpQualityMonitor.Web/AdminCredentials.cs').read_text()
        self.assertIn('File.Move(temporary, path, false)', credentials)
        self.assertIn('UnixFileMode.UserRead | UnixFileMode.UserWrite', credentials)

    def test_ci_runs_container_auth_lifecycle_and_real_browser(self):
        smoke = (ROOT / 'tests/server/smoke-container.sh').read_text()
        self.assertIn('python3 tests/server/auth_lifecycle.py', smoke)
        self.assertIn('node tests/server/browser-smoke.cjs --url', smoke)
        workflow = (ROOT / '.github/workflows/server-preview.yml').read_text()
        self.assertIn('IQM_BROWSER_SMOKE=1 bash tests/server/smoke-container.sh', workflow)
        runner = (ROOT / 'tests/server/auth_lifecycle.py').read_text()
        ast.parse(runner)
        self.assertIn("'volume', 'create', '--label', LABEL", runner)
        self.assertIn('Refusing to reuse a pre-existing Docker volume', runner)
        self.assertIn("'10001:10001:600'", runner)
        self.assertIn('Pre-reset cookie remained authorized', runner)
        self.assertIn('Offline reset altered a file besides renaming authentication', runner)
        self.assertIn('Competing instance overwrote authentication', runner)
        self.assertIn('Invalid persisted authentication was overwritten', runner)

    def test_csrf_auth_and_body_limit(self):
        text = (ROOT / 'src/IpQualityMonitor.Web/Program.cs').read_text()
        self.assertIn('RequireAuthorization()', text)
        self.assertIn('ValidateRequestAsync(context)', text)
        self.assertIn('MaxRequestBodySize = 32768', text)
        self.assertIn('RequireRateLimiting("login")', text)

    def test_healthcheck_exits_before_host_construction(self):
        text = (ROOT / 'src/IpQualityMonitor.Web/Program.cs').read_text()
        before = text.split('var builder = WebApplication.CreateBuilder', 1)[0]
        self.assertIn('--healthcheck', before)
        self.assertIn('return;', before)
        self.assertNotIn('new History', before)

    def test_fixed_upstream(self):
        lock = json.loads((ROOT / 'tools/mtr/UPSTREAM.json').read_text())
        self.assertEqual(lock['commit'], '7b017733aef06bb3d8e3573b2e964cc876644fad')
        self.assertEqual(len(lock['files']), 5)
        for value in lock['files'].values(): self.assertRegex(value, r'^[0-9a-f]{40}$')

    def test_patch_anchor_fails_closed(self):
        self.assertEqual(patch.once('ab', 'a', 'x'), 'xb')
        with self.assertRaises(ValueError): patch.once('aa', 'a', 'x')
        with self.assertRaises(ValueError): patch.once('ab', 'c', 'x')
        source = 'before if (x) { if (y) { run(); } } after'
        self.assertEqual(patch.replace_block(source, 'if (x)', 'replacement'), 'before replacement after')

    def test_patch_rejects_unknown_source_before_writing(self):
        with tempfile.TemporaryDirectory() as folder:
            file = Path(folder) / 'packet/command.c'; file.parent.mkdir(); file.write_text('not-upstream')
            with self.assertRaises(ValueError): patch.apply(Path(folder))
            self.assertEqual(file.read_text(), 'not-upstream')
            self.assertEqual(len(list(Path(folder).rglob('*'))), 2)

    def test_private_transport_limits_and_contract_gate(self):
        text = (ROOT / 'src/IpQualityMonitor.Linux/MtrPacketClient.cs').read_text()
        self.assertIn('new(64, 64)', text)
        self.assertIn('iqm-contract-v1', text)
        self.assertIn('iqm-raw-ip-4', text)
        self.assertIn('cancel-probe', text)
        self.assertIn('timeout-ms', text)
        self.assertIn('result.Length >= 8192', text)

    def test_no_terminal_commands_interpolated_into_shell(self):
        for name in ['MtrPacketClient.cs', 'LinuxNetworkContext.cs']:
            text = (ROOT / 'src/IpQualityMonitor.Linux' / name).read_text()
            self.assertIn('UseShellExecute = false', text)
            self.assertNotIn('"/bin/sh"', text)
        self.assertIn('ArgumentList.Add', (ROOT / 'src/IpQualityMonitor.Linux/LinuxNetworkContext.cs').read_text())

    def test_no_innerhtml_for_untrusted_target_labels(self):
        text = (ROOT / 'src/IpQualityMonitor.Web/wwwroot/app.js').read_text()
        self.assertNotIn('innerHTML', text)
        self.assertNotIn('insertAdjacentHTML', text)
        self.assertIn('textContent', text)

    @unittest.skipUnless(shutil.which('gcc'), 'gcc unavailable')
    def test_native_clock_and_timeout_arithmetic(self):
        # Compile the exact C fragments carried by patch.py, not a separately rewritten model.
        tree = ast.parse(PATCH.read_text())
        clock = None
        arithmetic = None
        for node in ast.walk(tree):
            if isinstance(node, ast.Assign) and isinstance(node.value, ast.Constant):
                if any(isinstance(t, ast.Name) and t.id == 'clock_header' for t in node.targets): clock = node.value.value
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == 'once':
                if len(node.args) == 3 and isinstance(node.args[2], ast.Constant):
                    val = node.args[2].value
                    if isinstance(val, str) and 'if (param->timeout_ms > 0)' in val: arithmetic = val
        self.assertIsNotNone(clock); self.assertIsNotNone(arithmetic)
        c = clock + '\n#include <assert.h>\n' + '''
struct probe { struct { struct timeval timeout_time; } platform; };
struct parameters { int timeout_ms; int timeout; };
static void expire(struct probe *probe, struct parameters *param) {
''' + arithmetic + '''
}
int main(void) {
    struct timeval before, after;
    iqm_gettime(&before, NULL); iqm_gettime(&after, NULL);
    assert(after.tv_sec > before.tv_sec || (after.tv_sec == before.tv_sec && after.tv_usec >= before.tv_usec));
    int cases[] = {1, 100, 1500, 5000, 60000};
    for (unsigned int i=0; i<sizeof(cases)/sizeof(cases[0]); i++) {
        struct probe p = {.platform.timeout_time = {.tv_sec=5,.tv_usec=750000}};
        struct parameters params = {.timeout_ms=cases[i],.timeout=3};
        expire(&p,&params);
        long long expected=5750000LL+1000LL*cases[i];
        assert(p.platform.timeout_time.tv_sec*1000000LL+p.platform.timeout_time.tv_usec == expected);
        assert(p.platform.timeout_time.tv_usec < 1000000);
    }
    struct probe p={.platform.timeout_time={.tv_sec=5,.tv_usec=750000}};
    struct parameters params={.timeout_ms=0,.timeout=3};
    expire(&p,&params); assert(p.platform.timeout_time.tv_sec==8 && p.platform.timeout_time.tv_usec==750000);
    return 0;
}
'''
        with tempfile.TemporaryDirectory() as folder:
            source=Path(folder)/'clock.c'; exe=Path(folder)/'clock'; source.write_text(c)
            subprocess.run(['gcc','-std=c11','-D_POSIX_C_SOURCE=200809L','-Wall','-Wextra','-Werror',str(source),'-o',str(exe)],check=True,capture_output=True)
            subprocess.run([str(exe)],check=True,capture_output=True)


if __name__ == '__main__': unittest.main()
