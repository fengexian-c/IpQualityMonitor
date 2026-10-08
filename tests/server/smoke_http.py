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
p.add_argument('--tcp-only', action='store_true')
a = p.parse_args()
base = a.url.rstrip('/')
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
csrf = ''

def request(path, method='GET', body=None):
    data = None if body is None else json.dumps(body).encode()
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
request('/api/auth/login', 'POST', {'password': password})
csrf = request('/api/auth/csrf')['token']
state = request('/api/overview')
assert state['network'] == 'bridge'
assert state['config']['profiles'] == [], 'Refusing to change an existing dataset'
policy = state['config']['monitoring']
policy['intervalSeconds'] = 1
policy['enableRoutes'] = not a.tcp_only
request('/api/settings', 'PUT', {'revision': state['config']['revision'], 'monitoring': policy})
state = request('/api/overview')
profile = request('/api/targets', 'POST', {'revision': state['config']['revision'], 'target':
    {'name': 'smoke-loopback-only', 'address': '127.0.0.1', 'mode': 'Tcp' if a.tcp_only else 'Both', 'port': 8080}})
state = request('/api/overview')
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
