#!/usr/bin/env python3
"""Destructive only to a NEW disposable smoke-test dataset; refuses any existing target."""
import argparse
import http.cookiejar
import json
import time
import urllib.request
import urllib.error

p = argparse.ArgumentParser()
p.add_argument('--url', required=True)
p.add_argument('--password-file', required=True)
p.add_argument('--username', default='admin', help='Exact case-sensitive administrator username')
p.add_argument('--tcp-only', action='store_true')
p.add_argument('--resume', action='store_true', help='Validate the existing disposable smoke dataset after restart')
a = p.parse_args()
base = a.url.rstrip('/')
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
csrf = ''

def request(path, method='GET', body=None):
    data = body if isinstance(body, bytes) else (None if body is None else json.dumps(body).encode())
    headers = {'Content-Type': 'application/json'}
    if method != 'GET': headers['X-CSRF-TOKEN'] = csrf
    result = opener.open(urllib.request.Request(base + path, data=data, headers=headers, method=method), timeout=10)
    text = result.read()
    return json.loads(text) if text else None

for attempt in range(60):
    try:
        request('/health/live')
        break
    except (OSError, urllib.error.URLError): time.sleep(1)
else: raise SystemExit('Server did not become live')
try:
    request('/api/overview')
    raise AssertionError('overview allowed without authentication')
except urllib.error.HTTPError as error:
    assert error.code == 401, error.code
csrf = request('/api/auth/csrf')['token']
with open(a.password_file) as f: password = f.read().rstrip('\r\n')
request('/api/auth/login', 'POST', {'username': a.username, 'password': password})
csrf = request('/api/auth/csrf')['token']
state = request('/api/overview')
assert state['network'] == 'bridge'
if a.resume:
    profiles = state['config']['profiles']
    assert len(profiles) == 1 and profiles[0]['name'] == 'smoke-loopback-only', 'Unexpected smoke dataset'
    profile = {'id': profiles[0]['id']}
else:
    assert state['config']['profiles'] == [], 'Refusing to change an existing dataset'
    revision = state['config']['revision']
    def rejected(path, method, body, status=400):
        try:
            request(path, method, body)
            raise AssertionError('Invalid input was accepted: ' + path)
        except urllib.error.HTTPError as error:
            assert error.code == status, (path, error.code, error.read().decode())
    valid_target = {'name': 'validation-only', 'address': '127.0.0.1', 'mode': 'Tcp', 'port': 8080}
    for body in [b'{broken', {}, {'revision': revision}, {'revision': revision, 'target': None},
                 {'target': valid_target},
                 {'revision': revision, 'target': dict(valid_target, name='x' * 101)},
                 {'revision': revision, 'target': dict(valid_target, mode='Unsupported')},
                 {'revision': revision, 'target': dict(valid_target, address='not-an-IP')}]:
        rejected('/api/targets', 'POST', body)
    for body in [{}, {'revision': revision}, {'revision': revision, 'monitoring': None}]:
        rejected('/api/settings', 'PUT', body)
    assert request('/api/overview')['config']['revision'] == revision
    print('PASS malformed, missing, null and invalid inputs do not mutate configuration')
    policy = state['config']['monitoring']
    policy['intervalSeconds'] = 1
    policy['enableRoutes'] = not a.tcp_only
    request('/api/settings', 'PUT', {'revision': revision, 'monitoring': policy})
    rejected('/api/settings', 'PUT', {'revision': revision, 'monitoring': policy}, 409)
    state = request('/api/overview')
    profile = request('/api/targets', 'POST', {'revision': state['config']['revision'], 'target':
        {'name': 'smoke-loopback-only', 'address': '127.0.0.1', 'mode': 'Tcp' if a.tcp_only else 'Both', 'port': 8080}})
    state = request('/api/overview')
    rejected('/api/targets/' + profile['id'] + '/run', 'POST', {'revision': state['config']['revision']})
    request('/api/targets/' + profile['id'] + '/run', 'POST', {'revision': state['config']['revision'], 'running': True})
for attempt in range(40):
    state = request('/api/overview')
    target = state['targets'][0]
    primary = target['primary']
    secondary = target['secondary']
    if primary and primary['last'] and primary['last']['status'] == 'Success' and (
        a.tcp_only or secondary and secondary['last'] and secondary['last']['status'] == 'Success'):
        break
    time.sleep(1)
else: raise AssertionError('No successful loopback samples: ' + json.dumps(state, ensure_ascii=False))
print('PASS bridge HTTP authentication + persistent background ' + ('TCP' if a.tcp_only else 'ICMP/TCP'))
# Missing CSRF must fail, including for an authenticated user.
old = csrf
csrf = ''
try:
    request('/api/targets/' + profile['id'] + '/route', 'POST')
    raise AssertionError('missing CSRF accepted')
except urllib.error.HTTPError as error:
    assert error.code == 400, error.code
csrf = old
print('PASS authenticated writes require CSRF')

request('/health/ready')
for protocol in (['Tcp'] if a.tcp_only else ['Tcp', 'Icmp']):
    for attempt in range(25):
        timeline = request('/api/targets/' + profile['id'] + '/timeline?protocol=' + protocol + '&days=1')['timeline']
        if timeline['total']['attempts'] > 0: break
        time.sleep(1)
    else: raise AssertionError('No persisted timeline samples for ' + protocol)
request('/api/targets/' + profile['id'] + '/events')
if not a.tcp_only:
    for attempt in range(30):
        routes = request('/api/targets/' + profile['id'] + '/routes')
        if routes: break
        time.sleep(1)
    else: raise AssertionError('No loopback route persisted')
print('PASS readiness and SQLite history queries' + ('' if a.tcp_only else ' and route persistence') + (' after restart' if a.resume else ''))
request('/api/auth/logout', 'POST')
assert request('/api/auth/session')['authenticated'] is False
print('PASS logout invalidates authenticated session')
