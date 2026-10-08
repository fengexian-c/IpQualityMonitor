#!/usr/bin/env python3
"""Real-image authentication lifecycle checks on newly created, disposable named volumes.

Run after building ipqualitymonitor:server-preview. Docker is required. No existing
volume is accepted; no public network target is probed. Every credential below is a
labeled test fixture. Docker command errors deliberately omit arguments and logs so
that a failed secrecy assertion cannot itself print a credential value.
"""
import argparse
import base64
import hashlib
import http.cookiejar
import json
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid

IMAGE = 'ipqualitymonitor:server-preview'
PREFIX = 'iqm-auth-smoke-' + uuid.uuid4().hex[:12]
LABEL = 'org.ipqualitymonitor.test=disposable-auth-lifecycle'
ENV_USER = 'Disposable.Env_Admin-01'
# Leading/trailing spaces are intentional: environment passwords must be exact.
ENV_PASSWORD = '  disposable-only-env-password  '
RESET_USER = 'Disposable.Reset_Admin-02'
RESET_PASSWORD = 'disposable-only-reset-password'
FILE_PASSWORD = '  disposable-only-file-password  '
LEGACY_PASSWORD = 'disposable-only-legacy-password'
POISON_USER = 'disposable invalid username!'
POISON_PASSWORD = 'disposable-only-ignored-password'
POISON_FILE = '/disposable-only-missing-password-file'
AUTH_FILE = '/data/admin-auth.json'


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def docker(*args, data=None):
    result = subprocess.run(['docker', *args], input=data, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError('Docker ' + args[0] + ' failed (details suppressed to protect fixture values)')
    return result.stdout


class Client:
    def __init__(self, base):
        self.base = base
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.csrf = ''

    def call(self, path, method='GET', body=None, raw=False):
        data = body if raw else (None if body is None else json.dumps(body).encode())
        headers = {'Content-Type': 'application/json'}
        if method != 'GET':
            headers['X-CSRF-TOKEN'] = self.csrf
        request = urllib.request.Request(self.base + path, data=data, headers=headers, method=method)
        try:
            response = self.opener.open(request, timeout=5)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            value = response.read()
            return response.status, value, response.headers

    def get(self, path):
        status, body, _ = self.call(path)
        require(status == 200, 'Expected successful read: ' + path)
        return json.loads(body)

    def token(self):
        self.csrf = self.get('/api/auth/csrf')['token']

    def login_body(self, body):
        # Exercise the production limiter; do not weaken it for the test suite.
        # The 5/minute window spans these negative cases, so a 429 is retried only
        # until that one production window expires.
        deadline = time.monotonic() + 70
        while True:
            status, value, headers = self.call('/api/auth/login', 'POST', body)
            if status != 429:
                return status, value, headers
            require(time.monotonic() < deadline, 'Login limiter did not reopen within its documented window')
            time.sleep(1)

    def login(self, username, password):
        self.token()
        status, _, _ = self.login_body({'username': username, 'password': password})
        require(status == 204, 'Expected fixture login to succeed')
        self.token()

    def write(self, path, body, method='POST'):
        status, value, _ = self.call(path, method, body)
        require(status in (200, 202, 204), 'Expected successful mutation: ' + path)
        return json.loads(value) if value else None


class Harness:
    def __init__(self):
        self.volumes = []
        self.containers = []
        self.secrets = {ENV_USER, ENV_PASSWORD, RESET_USER, RESET_PASSWORD,
                        FILE_PASSWORD, LEGACY_PASSWORD, POISON_USER, POISON_PASSWORD}
        self.count = 0

    def volume(self, scenario):
        name = PREFIX + '-' + scenario
        # A random, newly-created name is a hard safety boundary for destructive tests.
        existing = docker('volume', 'ls', '--format', '{{.Name}}').splitlines()
        require(name not in existing, 'Refusing to reuse a pre-existing Docker volume')
        docker('volume', 'create', '--label', LABEL, name)
        self.volumes.append(name)
        return name

    def volume_shell(self, volume, script, data=None):
        require(volume in self.volumes, 'Refusing access to an unowned volume')
        return docker('run', '--rm', '--network', 'none', '--read-only', '--cap-drop', 'ALL',
                      '--security-opt', 'no-new-privileges', '--user', '10001:10001',
                      '-v', volume + ':/data', '--entrypoint', '/bin/sh', '-i', IMAGE,
                      '-c', script, data=data)

    def put(self, volume, path, text):
        require(path in ('admin-auth.json', 'test-password'), 'Unexpected fixture path')
        self.volume_shell(volume, 'umask 077; cat > /data/' + path, text)

    def auth(self, volume):
        return self.volume_shell(volume, 'cat ' + AUTH_FILE)

    def no_auth(self, volume):
        self.volume_shell(volume, 'test ! -e ' + AUTH_FILE)

    def start(self, volume, env=None):
        require(volume in self.volumes, 'Refusing to start with an unowned volume')
        self.count += 1
        name = PREFIX + '-container-' + str(self.count)
        # Select the loopback port before startup: invalid configurations can exit
        # before Docker's dynamic-port inspection still has a live binding.
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        args = ['run', '-d', '--name', name, '--label', LABEL, '--network', 'bridge', '--init',
                '--cap-drop', 'ALL', '--cap-add', 'NET_RAW', '--read-only',
                '--tmpfs', '/tmp:rw,nosuid,nodev,size=64m', '-p', '127.0.0.1:' + str(port) + ':8080', '-v', volume + ':/data']
        for key, value in (env or {}).items():
            args += ['-e', key + '=' + value]
            if key in ('IPQUALITY_ADMIN_USERNAME', 'IPQUALITY_ADMIN_PASSWORD') and value.strip():
                self.secrets.add(value)
        self.containers.append(name)
        docker(*args, IMAGE)
        return name, Client('http://127.0.0.1:' + str(port))

    def state(self, name):
        return json.loads(docker('inspect', '--format', '{{json .State}}', name))

    def logs_safe(self, name):
        # docker logs emits application stderr on Docker stderr, so inspect both.
        result = subprocess.run(['docker', 'logs', name], capture_output=True, text=True)
        require(result.returncode == 0, 'Could not inspect container logs')
        logs = result.stdout + result.stderr
        for value in self.secrets:
            # Empty values contain no secret; other fixture values must not appear.
            require(value not in logs, 'Container logs exposed a bootstrap credential fixture')
        return logs

    def live(self, name, client):
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            require(self.state(name)['Running'], 'Container exited before becoming live')
            try:
                if client.call('/health/live')[0] == 200:
                    self.logs_safe(name)
                    return
            except (OSError, urllib.error.URLError):
                pass
            time.sleep(0.25)
        raise AssertionError('Container did not become live')

    def fails_closed(self, name, client):
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            try:
                client.call('/health/live')
            except (OSError, urllib.error.URLError):
                pass
            else:
                raise AssertionError('Invalid bootstrap served an HTTP response before failing')
            state = self.state(name)
            if not state['Running']:
                require(state['ExitCode'] != 0, 'Invalid bootstrap exited successfully')
                logs = self.logs_safe(name)
                require('Now listening on:' not in logs, 'Invalid bootstrap reached listener startup')
                return
            time.sleep(0.1)
        raise AssertionError('Invalid bootstrap did not fail before listening')

    def remove(self, name):
        if self.state(name)['Running']:
            docker('stop', '--time', '45', name)
            require(self.state(name)['ExitCode'] == 0, 'SIGTERM was not graceful')
        self.logs_safe(name)
        docker('rm', name)
        self.containers.remove(name)

    def cleanup(self):
        failures = []
        for name in reversed(self.containers):
            try:
                self.logs_safe(name)
            except (AssertionError, RuntimeError):
                failures.append(name)
            subprocess.run(['docker', 'rm', '-f', name], capture_output=True, text=True)
        for volume in reversed(self.volumes):
            subprocess.run(['docker', 'volume', 'rm', volume], capture_output=True, text=True)
        require(not failures, 'Credential log check failed during cleanup')


def auth_contract(client, username, password):
    require(client.call('/api/overview')[0] == 401, 'Protected API allowed anonymous access')
    client.token()
    wrong_name = client.login_body({'username': username.swapcase(), 'password': password})
    wrong_password = client.login_body({'username': username, 'password': password.strip()})
    require(wrong_name[:2] == wrong_password[:2] and wrong_name[0] == 401,
            'Wrong username and wrong password must have identical unauthorized responses')
    require('no-store' in wrong_name[2].get('Cache-Control', ''), 'Authentication response may be cached')
    for body, expected in (({}, 400), ({'password': password}, 400), ({'username': username}, 400),
                           ({'username': None, 'password': password}, 401), ({'username': username, 'password': None}, 401),
                           ({'username': '', 'password': password}, 401), ({'username': username, 'password': ''}, 401)):
        status, _, _ = client.login_body(body)
        require(status == expected, 'Missing, null or password-only login returned the wrong status')
        require(client.get('/api/auth/session')['authenticated'] is False, 'Rejected login created a session')
    client.login(username, password)
    cookies = [cookie for cookie in client.jar if cookie.name == 'iqm.session']
    require(len(cookies) == 1 and any(cookies[0].has_nonstandard_attr(key) for key in ('HttpOnly', 'httponly')),
            'Session cookie must be HttpOnly')
    print('PASS HTTP exact username/password, generic errors, missing/null/password-only rejection, CSRF and production login limiter')


def seed_history(client):
    state = client.get('/api/overview')
    require(state['config']['profiles'] == [], 'Refusing to mutate a nonempty fixture dataset')
    policy = state['config']['monitoring']
    policy['intervalSeconds'] = 1
    policy['enableRoutes'] = False
    client.write('/api/settings', {'revision': state['config']['revision'], 'monitoring': policy}, 'PUT')
    state = client.get('/api/overview')
    target = client.write('/api/targets', {'revision': state['config']['revision'], 'target': {
        'name': 'auth-lifecycle-loopback-only', 'address': '127.0.0.1', 'mode': 'Tcp', 'port': 8080}})
    state = client.get('/api/overview')
    client.write('/api/targets/' + target['id'] + '/run', {'revision': state['config']['revision'], 'running': True})
    path = '/api/targets/' + target['id'] + '/timeline?protocol=Tcp&days=1'
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        timeline = client.get(path)['timeline']
        if timeline['total']['successes'] > 0:
            break
        time.sleep(1)
    else:
        raise AssertionError('No real loopback TCP history sample persisted')
    state = client.get('/api/overview')
    client.write('/api/targets/' + target['id'] + '/run', {'revision': state['config']['revision'], 'running': False})
    # Await the final worker write before comparing the paused persisted history.
    time.sleep(2)
    state = client.get('/api/overview')
    require(state['targets'][0]['running'] is False, 'Fixture target was not paused')
    return state, path, client.get(path)['timeline']['total']


def preserved(client, baseline, history_path, total):
    state = client.get('/api/overview')
    require(state['site'] == baseline['site'], 'Site identity changed')
    require(state['config'] == baseline['config'], 'Stored settings/targets changed')
    require(state['targets'][0]['running'] is False, 'Paused fixture resumed unexpectedly')
    require(client.get(history_path)['timeline']['total'] == total, 'Persisted TCP history changed or disappeared')


def main_lifecycle(h):
    volume = h.volume('env')
    name, client = h.start(volume, {'IPQUALITY_ADMIN_USERNAME': ENV_USER, 'IPQUALITY_ADMIN_PASSWORD': ENV_PASSWORD})
    h.live(name, client)
    require(docker('exec', name, 'id', '-u').strip() == '10001', 'Web process did not run as UID 10001')
    require(h.volume_shell(volume, 'stat -c "%u:%g:%a" ' + AUTH_FILE).strip() == '10001:10001:600',
            'Persisted credentials are not owned by 10001 with mode 0600')
    original_auth = h.auth(volume)
    record = json.loads(original_auth)
    require(record['SchemaVersion'] == 2 and record['Username'] == ENV_USER, 'Fresh credentials did not persist schema 2 username')
    require(ENV_PASSWORD not in original_auth, 'Persisted credentials exposed the password')
    auth_contract(client, ENV_USER, ENV_PASSWORD)
    baseline, history_path, total = seed_history(client)
    h.remove(name)

    # Create a NEW container, retaining only the named data volume. No bootstrap
    # variables or password file mount may accidentally make persistence pass.
    name, recreated = h.start(volume)
    h.live(name, recreated)
    runtime_env = json.loads(docker('inspect', '--format', '{{json .Config.Env}}', name))
    require(not any(value.startswith('IPQUALITY_ADMIN_') for value in runtime_env), 'Recreated container still received bootstrap configuration')
    client.base = recreated.base
    require(client.get('/api/auth/session')['authenticated'] is True, 'Existing cookie was lost during normal recreation')
    recreated.login(ENV_USER, ENV_PASSWORD)
    preserved(recreated, baseline, history_path, total)
    require(h.auth(volume) == original_auth, 'Normal recreation rewrote persisted credentials')
    print('PASS fresh named volume, environment bootstrap, UID 10001 / 0600 and recreation without any bootstrap preserve auth/site/history')

    # A competing writer must fail on the dataset lease, even with valid alternate
    # bootstrap input. The first owner must remain usable and unchanged.
    competitor, blocked = h.start(volume, {'IPQUALITY_ADMIN_USERNAME': RESET_USER, 'IPQUALITY_ADMIN_PASSWORD': RESET_PASSWORD})
    h.fails_closed(competitor, blocked)
    require(h.auth(volume) == original_auth, 'Competing instance overwrote authentication')
    preserved(recreated, baseline, history_path, total)
    h.remove(competitor)
    h.remove(name)
    print('PASS two instances cannot share/write one named volume')

    poison = {'IPQUALITY_ADMIN_USERNAME': POISON_USER, 'IPQUALITY_ADMIN_PASSWORD': '', 'IPQUALITY_ADMIN_PASSWORD_FILE': POISON_FILE}
    name, ignored = h.start(volume, poison)
    h.live(name, ignored)
    ignored.login(ENV_USER, ENV_PASSWORD)
    preserved(ignored, baseline, history_path, total)
    require(h.auth(volume) == original_auth, 'Ignored bootstrap changed existing credentials')
    h.remove(name)
    print('PASS valid persisted credentials ignore all invalid/conflicting bootstrap settings')

    # This is the documented offline reset: move ONLY the auth file. Prove all
    # other files (including Data Protection keys and SQLite history) are untouched.
    files = 'cd /data; find . -type f -exec sha256sum {} \\; | sort'
    before = h.volume_shell(volume, files)
    h.volume_shell(volume, 'mv /data/admin-auth.json /data/admin-auth.before-reset.json')
    after = h.volume_shell(volume, files)
    require(after.replace('./admin-auth.before-reset.json', './admin-auth.json').splitlines() == before.splitlines(),
            'Offline reset altered a file besides renaming authentication')
    name, reset = h.start(volume, {'IPQUALITY_ADMIN_USERNAME': RESET_USER, 'IPQUALITY_ADMIN_PASSWORD': RESET_PASSWORD})
    h.live(name, reset)
    reset_record = json.loads(h.auth(volume))
    require(reset_record['Salt'] != record['Salt'], 'Reset reused the prior authentication salt')
    require(reset_record['Username'] == RESET_USER, 'Reset did not apply the new username')
    client.base = reset.base  # This jar still holds the pre-reset authenticated cookie.
    require(client.call('/api/overview')[0] == 401, 'Pre-reset cookie remained authorized')
    require(client.get('/api/auth/session')['authenticated'] is False, 'Pre-reset cookie retained its session')
    reset.token()
    require(reset.login_body({'username': ENV_USER, 'password': ENV_PASSWORD})[0] == 401, 'Old credentials survived reset')
    reset.login(RESET_USER, RESET_PASSWORD)
    preserved(reset, baseline, history_path, total)
    require(h.volume_shell(volume, 'cat /data/admin-auth.before-reset.json') == original_auth, 'Offline backup changed')
    h.remove(name)
    print('PASS offline auth-file-only reset invalidates old credentials/cookies and preserves site/settings/history')


def compatible_sources(h):
    volume = h.volume('file')
    h.put(volume, 'test-password', FILE_PASSWORD + '\r\n')
    name, client = h.start(volume, {'IPQUALITY_ADMIN_PASSWORD_FILE': '/data/test-password'})
    h.live(name, client)
    client.login('admin', FILE_PASSWORD)
    require(json.loads(h.auth(volume))['Username'] == 'admin', 'Absent username did not default to admin')
    h.remove(name)
    print('PASS password-file bootstrap trims terminal CR/LF and absent username defaults to admin')

    volume = h.volume('legacy')
    salt = hashlib.sha256(b'disposable-legacy-fixture-salt').digest()
    require(len(salt) == 32, 'Invalid legacy fixture salt')
    legacy = json.dumps({'SchemaVersion': 1, 'Iterations': 210000, 'Salt': base64.b64encode(salt).decode(),
                         'Hash': base64.b64encode(hashlib.pbkdf2_hmac('sha512', LEGACY_PASSWORD.encode(), salt, 210000, 32)).decode()}, indent=2)
    h.put(volume, 'admin-auth.json', legacy)
    name, client = h.start(volume, {'IPQUALITY_ADMIN_USERNAME': POISON_USER, 'IPQUALITY_ADMIN_PASSWORD': POISON_PASSWORD,
                                  'IPQUALITY_ADMIN_PASSWORD_FILE': POISON_FILE})
    h.live(name, client)
    client.token()
    require(client.login_body({'username': 'Admin', 'password': LEGACY_PASSWORD})[0] == 401, 'Legacy username comparison was not case-sensitive')
    client.login('admin', LEGACY_PASSWORD)
    require(h.auth(volume) == legacy, 'Reading schema 1 silently migrated or rewrote its record')
    h.remove(name)
    print('PASS legacy schema-1 credentials authenticate as admin unchanged despite conflicting bootstrap')

    # Boundary acceptance through real containers complements the exact-length
    # unit cases and confirms no shell/environment transformation is involved.
    for length in (16, 256):
        volume = h.volume('length-' + str(length))
        password = ('test-only-' + 'P' * length)[:length]
        name, client = h.start(volume, {'IPQUALITY_ADMIN_PASSWORD': password})
        h.live(name, client)
        client.login('admin', password)
        h.remove(name)
    print('PASS environment password lengths 16 and 256 accepted with default admin')


def invalid_bootstrap(h):
    valid = {'IPQUALITY_ADMIN_USERNAME': ENV_USER, 'IPQUALITY_ADMIN_PASSWORD': ENV_PASSWORD}
    cases = [('missing-password', {}), ('empty-password', dict(valid, IPQUALITY_ADMIN_PASSWORD='')),
             ('short-password', dict(valid, IPQUALITY_ADMIN_PASSWORD='test-only-short')),
             ('long-password', dict(valid, IPQUALITY_ADMIN_PASSWORD='test-only-' + 'P' * 248)),
             ('empty-file', {'IPQUALITY_ADMIN_PASSWORD_FILE': ''}),
             ('missing-file', {'IPQUALITY_ADMIN_PASSWORD_FILE': POISON_FILE}),
             ('directory-file', {'IPQUALITY_ADMIN_PASSWORD_FILE': '/data'}),
             ('both-empty', {'IPQUALITY_ADMIN_PASSWORD': '', 'IPQUALITY_ADMIN_PASSWORD_FILE': ''}),
             ('password-empty-file', dict(valid, IPQUALITY_ADMIN_PASSWORD_FILE='')),
             ('empty-password-file', {'IPQUALITY_ADMIN_PASSWORD': '', 'IPQUALITY_ADMIN_PASSWORD_FILE': '/data/test-password'}),
             ('both-valid', dict(valid, IPQUALITY_ADMIN_PASSWORD_FILE='/data/test-password'))]
    for suffix, username in [('empty', ''), ('whitespace', ' '), ('space', POISON_USER),
                             ('unicode', 'disposable-üser'), ('punctuation', 'disposable@user'),
                             ('path', 'disposable/user'), ('long', 'disposable-' + 'u' * 54)]:
        cases.append(('username-' + suffix, dict(valid, IPQUALITY_ADMIN_USERNAME=username)))
    for scenario, env in cases:
        volume = h.volume('invalid-' + scenario)
        h.put(volume, 'test-password', FILE_PASSWORD + '\n')
        name, client = h.start(volume, env)
        h.fails_closed(name, client)
        h.no_auth(volume)
        h.remove(name)
    for scenario, content in [('empty', ''), ('short', 'test-only-short\n'), ('large', 'test-only-' + 'F' * 4096)]:
        volume = h.volume('invalid-file-' + scenario)
        h.put(volume, 'test-password', content)
        if content:
            h.secrets.add(content.rstrip('\r\n'))
        name, client = h.start(volume, {'IPQUALITY_ADMIN_PASSWORD_FILE': '/data/test-password'})
        h.fails_closed(name, client)
        h.no_auth(volume)
        h.remove(name)
    print('PASS missing/blank/conflicting/invalid bootstrap fails before listening without persisting auth or logging fixture values')


def invalid_records(h):
    salt = base64.b64encode(b'D' * 32).decode()
    record = {'SchemaVersion': 2, 'Iterations': 210000, 'Salt': salt, 'Hash': salt, 'Username': ENV_USER}
    cases = [('malformed', '{disposable-malformed-auth-record'), ('null', 'null'),
             ('unknown-schema', json.dumps(dict(record, SchemaVersion=99))),
             ('missing-username', json.dumps({key: value for key, value in record.items() if key != 'Username'})),
             ('invalid-username', json.dumps(dict(record, Username=POISON_USER))),
             ('null-username', json.dumps(dict(record, Username=None))),
             ('invalid-hash', json.dumps(dict(record, Hash='disposable-invalid-base64!'))),
             ('bad-salt-length', json.dumps(dict(record, Salt=base64.b64encode(b'short').decode()))),
             ('bad-iterations', json.dumps(dict(record, Iterations=1)))]
    for scenario, content in cases:
        volume = h.volume('record-' + scenario)
        h.put(volume, 'admin-auth.json', content)
        name, client = h.start(volume, {'IPQUALITY_ADMIN_USERNAME': RESET_USER, 'IPQUALITY_ADMIN_PASSWORD': RESET_PASSWORD})
        h.fails_closed(name, client)
        require(h.auth(volume) == content, 'Invalid persisted authentication was overwritten: ' + scenario)
        h.remove(name)
    print('PASS malformed/unknown/invalid persisted authentication fails before listening and is never overwritten')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    h = Harness()
    try:
        main_lifecycle(h)
        compatible_sources(h)
        invalid_bootstrap(h)
        invalid_records(h)
    finally:
        h.cleanup()
    print('PASS complete disposable Docker authentication lifecycle')


if __name__ == '__main__':
    main()
