#!/usr/bin/env node
'use strict';
/**
 * Browser regressions for the actual wwwroot files, with deterministic mocked APIs.
 * Requires Node 20+ and Playwright: npm install --no-save playwright@1.58.2
 * Install Chromium: npx playwright install --with-deps chromium
 * Run: node tests/server/browser-smoke.cjs
 * Optional installed browser: IQM_BROWSER_EXECUTABLE=/usr/bin/chromium
 * Optional real-server auth (no target/settings mutations):
 *   node tests/server/browser-smoke.cjs --url http://127.0.0.1:8080 --password-file /tmp/iqm-password
 * All mock traffic stays on a disposable loopback HTTP server. No public IP is probed.
 * Lifecycle tests explicitly dispatch persisted pagehide/pageshow, rather than claiming
 * that a browser's discretionary back/forward cache actually admitted the page.
 */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
const webroot = path.join(root, 'src/IpQualityMonitor.Web/wwwroot');
const options = {};
for (let i = 2; i < process.argv.length; i += 2) {
  const key = process.argv[i];
  if (!['--url', '--password-file', '--screenshots'].includes(key) || !process.argv[i + 1])
    throw new Error('Usage: browser-smoke.cjs [--url URL --password-file FILE] [--screenshots DIRECTORY]');
  options[key.slice(2)] = process.argv[i + 1];
}
if (!!options.url !== !!options['password-file']) throw new Error('--url and --password-file must be supplied together');
if (options.url && !['127.0.0.1', 'localhost', '[::1]'].includes(new URL(options.url).hostname))
  throw new Error('Real-server authentication smoke is restricted to a disposable loopback server');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function waitForGate(gate) { await eventually(() => gate.used, `Delayed ${gate.method} ${gate.pathname} entered`); }
const clone = value => JSON.parse(JSON.stringify(value));
const password = 'browser-smoke-disposable-password';
const now = '2026-10-08T00:00:00Z';
const policy = { intervalSeconds: 5, timeoutMilliseconds: 3000, routeMinutes: 10,
  routeMaxHops: 32, routeTimeoutMs: 1500, routeBudgetSeconds: 60, enableRoutes: true };
const profile = (id, name, mode) => ({ id, name, address: '127.0.0.1', mode, port: 8080, resumeOnLaunch: false });
function timeline(id, count) {
  count ??= id === 'target-a' ? 11 : 22;
  return { timeline: { from: '2026-10-07T00:00:00Z', until: now, stepMinutes: 5,
    total: { attempts: count, successes: count, localErrors: 0, availability: 100, average: count },
    points: [{ start: '2026-10-07T00:00:00Z', attempts: 1, successes: 1, average: count, minimum: count, maximum: count },
      { start: '2026-10-07T00:05:00Z', attempts: 0, successes: 0, average: null, minimum: null, maximum: null },
      { start: '2026-10-07T00:10:00Z', attempts: 1, successes: 0, average: null, minimum: null, maximum: null }],
    hours: [{ start: '2026-10-07T00:00:00Z', attempts: count, successes: count, localErrors: 0, availability: 100, average: count },
      { start: '2026-10-07T01:00:00Z', attempts: 0, successes: 0, localErrors: 1, availability: null, average: null }] } };
}

class MockApi {
  constructor(authenticated = true) {
    this.authenticated = authenticated;
    this.requests = [];
    this.holds = [];
    this.state = { site: { id: 'browser-site-id', name: 'Browser fixture' }, config: { revision: 1, monitoring: clone(policy) },
      targets: [profile('target-a', 'Alpha', 'Both'), profile('target-b', 'Beta', 'Tcp')]
        .map(profile => ({ profile, running: false, primary: null, secondary: null })),
      serverTime: now, timeZone: 'Asia/Shanghai', error: null, analysisError: null, probeError: null };
  }
  count(method, pathname) { return this.requests.filter(r => r.method === method && r.pathname === pathname).length; }
  hold(method, pathname, replacement) {
    let release, markEntered;
    const pending = new Promise(resolve => { release = resolve; });
    const entered = new Promise(resolve => { markEntered = resolve; });
    const hold = { method, pathname, replacement, pending, entered, release, markEntered, used: false };
    this.holds.push(hold);
    return hold;
  }
  reply(request) {
    const { pathname, method, body } = request;
    if (pathname === '/api/auth/session') return [200, { authenticated: this.authenticated }];
    if (pathname === '/api/auth/csrf') return [200, { token: 'test-only-csrf-token' }];
    if (pathname === '/api/auth/login') {
      this.authenticated = body.password === password;
      return this.authenticated ? [204] : [401, { error: 'Invalid password' }];
    }
    if (!this.authenticated) return [401, { error: 'Expired session' }];
    if (pathname === '/api/auth/logout') { this.authenticated = false; return [204]; }
    if (pathname === '/api/overview') return [200, clone(this.state)];
    if (pathname === '/api/targets' && method === 'POST') {
      const id = 'target-added';
      this.state.targets.push({ profile: { id, ...body.target, resumeOnLaunch: false }, running: false });
      this.state.config.revision++;
      return [200, { id }];
    }
    if (pathname === '/api/settings' && method === 'PUT') {
      this.state.config.monitoring = clone(body.monitoring); this.state.config.revision++;
      return [204];
    }
    const target = pathname.match(/^\/api\/targets\/([^/]+)(?:\/(\w+))?$/);
    if (target) {
      const [, id, action] = target;
      if (action === 'timeline') return [200, timeline(id)];
      if (action === 'routes') return [200, [{ id: 'route-' + id, started: now, outcome: 'Completed',
        contextDescription: 'Fixture loopback', probes: [{ ttl: 1, status: 0, address: '127.0.0.1', rttMs: 1.25 },
          { ttl: 2, status: 11010, address: null, rttMs: null }, { ttl: 3, status: 11013, address: '127.0.0.2', rttMs: 2.5 }] }]];
      if (action === 'events') return [200, [{ time: now, kind: 'Fixture', detail: 'Event for ' + id }]];
      if (action === 'route') return [202];
      if (action === 'run') {
        const entry = this.state.targets.find(x => x.profile.id === id);
        entry.running = body.running; this.state.config.revision++; return [204];
      }
      if (method === 'DELETE') {
        this.state.targets = this.state.targets.filter(x => x.profile.id !== id);
        this.state.config.revision++; return [204];
      }
    }
    throw new Error(`Unexpected mocked API request ${method} ${pathname}`);
  }
  async attach(page) {
    await page.route('**/api/**', async route => {
      const req = route.request();
      const request = { pathname: new URL(req.url()).pathname, url: req.url(), method: req.method(),
        body: req.postData() ? JSON.parse(req.postData()) : null, headers: req.headers() };
      this.requests.push(request);
      const hold = this.holds.find(x => !x.used && x.method === request.method && x.pathname === request.pathname);
      const response = hold?.replacement || this.reply(request);
      if (hold) { hold.used = true; hold.markEntered(); await hold.pending; }
      const [status, body] = response;
      // A canceled detail request may have gone away by the time its delayed response is released.
      try { await route.fulfill({ status, contentType: 'application/json', body: body === undefined ? '' : JSON.stringify(body) }); }
      catch (error) { if (!/closed|disposed|cancel|intercept|already handled/i.test(error.message)) throw error; }
    });
  }
}

async function eventually(condition, description, timeout = 5000) {
  const until = Date.now() + timeout;
  do { if (await condition()) return; await sleep(25); } while (Date.now() < until);
  throw new Error('Timed out: ' + description);
}
async function visible(page, selector) { await page.locator(selector).waitFor({ state: 'visible' }); }
async function hidden(page, selector) { await page.locator(selector).waitFor({ state: 'hidden' }); }
async function textIncludes(page, selector, text) {
  await eventually(async () => (await page.locator(selector).textContent()).includes(text), `${selector} contains ${text}`);
}
async function doubleSubmit(page, selector) {
  await page.locator(selector).evaluate(form => { form.requestSubmit(); form.requestSubmit(); });
}
async function doubleClick(page, selector) {
  await page.locator(selector).evaluate(button => { button.click(); button.click(); });
}
async function restore(page) {
  await page.evaluate(() => {
    window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }));
    window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
  });
}

async function main() {
  const server = http.createServer((req, res) => {
    const pathname = new URL(req.url, 'http://localhost').pathname;
    if (pathname === '/away') { res.setHeader('Content-Type', 'text/html'); res.end('<!doctype html><title>Away</title><p>Navigation fixture</p>'); return; }
    const name = pathname === '/' ? 'index.html' : pathname.slice(1);
    if (!['index.html', 'app.js', 'style.css'].includes(name)) { res.writeHead(404); res.end(); return; }
    res.setHeader('Content-Type', name.endsWith('.js') ? 'text/javascript' : name.endsWith('.css') ? 'text/css' : 'text/html');
    res.end(fs.readFileSync(path.join(webroot, name)));
  });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const base = `http://127.0.0.1:${server.address().port}`;
  let browser, failures = 0, passed = 0;
  const launch = { headless: true };
  if (process.env.IQM_BROWSER_EXECUTABLE) launch.executablePath = process.env.IQM_BROWSER_EXECUTABLE;
  try {
    browser = await chromium.launch(launch);
    console.log(`Browser: Chromium ${browser.version()}`);
    const test = async (name, body, settings = {}) => {
      const context = await browser.newContext({ viewport: settings.viewport || { width: 1280, height: 1000 } });
      const page = await context.newPage(); page.setDefaultTimeout(7000);
      const errors = []; page.on('pageerror', error => errors.push(error.message));
      const mock = new MockApi(settings.authenticated ?? true);
      if (settings.ignoreAbort) {
        // Exercise generation guards even when cancellation loses a completion race.
        await page.addInitScript(() => {
          const originalFetch = window.fetch.bind(window);
          window.fetch = (url, init = {}) => { const { signal, ...rest } = init; return originalFetch(url, rest); };
        });
      }
      await mock.attach(page);
      try {
        await page.goto(base);
        await visible(page, mock.authenticated ? '#workspace' : '#login');
        if (mock.authenticated) await textIncludes(page, '#target-count', '2 / 20');
        await body({ page, mock, context });
        assert.deepEqual(errors, [], 'No uncaught browser exceptions');
        console.log('PASS ' + name); passed++;
      } catch (error) {
        console.error('FAIL ' + name + ': ' + error.message); failures++;
        if (options.screenshots) {
          fs.mkdirSync(options.screenshots, { recursive: true });
          await page.screenshot({ path: path.join(options.screenshots, name.replace(/[^a-z0-9]+/gi, '-') + '.png'), fullPage: true }).catch(() => {});
        }
      } finally {
        mock.holds.forEach(hold => hold.release()); await context.close();
      }
    };

    await test('login failure, repeated submit, logout and private DOM clearing', async ({ page, mock }) => {
      await page.fill('#password', 'invalid-password'); await page.locator('#login-form button').click();
      await textIncludes(page, '#message', '登录'); await visible(page, '#login');
      await page.fill('#password', password);
      const gate = mock.hold('POST', '/api/auth/login');
      await doubleSubmit(page, '#login-form'); await waitForGate(gate); await sleep(100);
      assert.equal(mock.count('POST', '/api/auth/login'), 2, 'One failed login and only one pending retry');
      gate.release(); await visible(page, '#workspace'); await textIncludes(page, '#target-count', '2 / 20');
      assert.equal(await page.inputValue('#password'), '', 'Password cleared after login');
      await page.getByRole('button', { name: 'Alpha', exact: true }).click(); await textIncludes(page, '#totals', '样本 11');
      const logout = mock.hold('POST', '/api/auth/logout');
      await doubleClick(page, '#logout'); await waitForGate(logout); await sleep(100);
      assert.equal(mock.count('POST', '/api/auth/logout'), 1, 'Logout is deduplicated');
      logout.release(); await visible(page, '#login'); await hidden(page, '#workspace');
      assert.equal(await page.locator('#targets').textContent(), '', 'Protected target rows cleared');
      assert.equal(await page.locator('#events').textContent(), '', 'Protected events cleared');
      assert.equal(await page.locator('#totals').textContent(), '', 'Protected summary cleared');
      for (const request of mock.requests.filter(r => r.method !== 'GET'))
        assert.equal(request.headers['x-csrf-token'], 'test-only-csrf-token', 'Mutation includes CSRF token');
    }, { authenticated: false });

    await test('add target repeated submit', async ({ page, mock }) => {
      await page.locator('summary').filter({ hasText: '添加目标' }).click();
      await page.locator('#add-form [name=name]').fill('<img src=x onerror=alert(1)>');
      await page.locator('#add-form [name=address]').fill('127.0.0.1');
      const gate = mock.hold('POST', '/api/targets');
      await doubleSubmit(page, '#add-form'); await waitForGate(gate); await sleep(100);
      assert.equal(mock.count('POST', '/api/targets'), 1, 'Only one target created');
      gate.release(); await textIncludes(page, '#target-count', '3 / 20');
      assert.equal(await page.locator('#targets img').count(), 0, 'Target labels are plain text');
      assert.ok((await page.locator('#targets').textContent()).includes('<img src=x onerror=alert(1)>'));
    });

    await test('settings native validation and repeated save', async ({ page, mock }) => {
      // The closed form is already populated by the initial overview. Waiting for
      // its value alone does not wait for the queued <details> toggle handler, which
      // refreshes those values when opened. Wait for that handler before editing.
      await page.locator('#settings').evaluate(details => {
        details.dataset.smokeOpened = 'pending';
        details.addEventListener('toggle', () => {
          if (details.open) details.dataset.smokeOpened = 'ready';
        }, { once: true });
      });
      await page.locator('#settings summary').click();
      await eventually(async () => (await page.locator('#settings').getAttribute('data-smoke-opened')) === 'ready', 'Settings opening handler finished');
      const interval = page.locator('#settings-form [name=intervalSeconds]');
      assert.equal(await interval.inputValue(), '5', 'Settings populated');
      const invalidState = () => interval.evaluate(input => ({
        value: input.value, min: input.min, valid: input.validity.valid, rangeUnderflow: input.validity.rangeUnderflow
      }));
      const expectedInvalid = { value: '0', min: '1', valid: false, rangeUnderflow: true };
      await interval.fill('0');
      // Reproduce an opening event delivered after the first edit, without relying
      // on browser scheduling: a queued toggle must not overwrite unsaved input.
      await page.locator('#settings').evaluate(details => details.dispatchEvent(new Event('toggle')));
      assert.deepEqual(await invalidState(), expectedInvalid, 'Late toggle preserves invalid edited value before Save');
      await page.locator('#settings-form button').click();
      assert.deepEqual(await invalidState(), expectedInvalid, 'Save preserves invalid input and native validation');
      assert.equal(mock.count('PUT', '/api/settings'), 0, 'Invalid settings were not submitted');
      await interval.fill('2');
      const gate = mock.hold('PUT', '/api/settings');
      await doubleSubmit(page, '#settings-form'); await waitForGate(gate); await sleep(100);
      assert.equal(mock.count('PUT', '/api/settings'), 1, 'Only one settings save');
      gate.release(); await eventually(() => page.locator('#settings').evaluate(details => !details.open), 'Saved settings closed');
      assert.equal(mock.state.config.monitoring.intervalSeconds, 2);
    });

    await test('run duplicate suppression survives overview refresh', async ({ page, mock }) => {
      const gate = mock.hold('POST', '/api/targets/target-a/run');
      const row = page.locator('#targets tr').filter({ hasText: 'Alpha' });
      await row.getByRole('button', { name: '开始', exact: true }).click(); await waitForGate(gate);
      const previous = mock.count('GET', '/api/overview');
      await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
      await eventually(() => mock.count('GET', '/api/overview') > previous, 'Refreshed overview during run');
      await sleep(100);
      const run = row.locator('button.secondary');
      assert.equal(await run.isDisabled(), true, 'Replacement row retains pending operation lock');
      await run.evaluate(button => button.click());
      assert.equal(mock.count('POST', '/api/targets/target-a/run'), 1);
      gate.release(); await eventually(async () => await row.getByRole('button', { name: '暂停', exact: true }).count() === 1, 'Run reflected');
    });

    await test('route repeated click and remove cancellation', async ({ page, mock }) => {
      // The UI permits manual route requests only for running, route-enabled targets.
      mock.state.targets[0].running = true;
      await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
      await eventually(async () => await page.locator('#targets tr').filter({ hasText: 'Alpha' }).getByRole('button', { name: '暂停', exact: true }).count() === 1, 'Running state refreshed');
      await page.getByRole('button', { name: 'Alpha', exact: true }).click(); await textIncludes(page, '#totals', '样本 11');
      const gate = mock.hold('POST', '/api/targets/target-a/route');
      await doubleClick(page, '#probe-route'); await waitForGate(gate); await sleep(100);
      assert.equal(mock.count('POST', '/api/targets/target-a/route'), 1, 'Only one manual route request');
      gate.release();
      page.once('dialog', dialog => dialog.dismiss());
      await page.locator('#targets tr').filter({ hasText: 'Alpha' }).getByRole('button', { name: '移除', exact: true }).click();
      assert.equal(mock.count('DELETE', '/api/targets/target-a'), 0, 'Cancel does not remove target');
      await visible(page, '#detail'); assert.equal(await page.locator('#detail-name').textContent(), 'Alpha');
    });

    await test('target selection clears prior data and rejects stale detail', async ({ page, mock }) => {
      await page.getByRole('button', { name: 'Alpha', exact: true }).click(); await textIncludes(page, '#totals', '样本 11');
      const beta = mock.hold('GET', '/api/targets/target-b/timeline');
      await page.getByRole('button', { name: 'Beta', exact: true }).click(); await waitForGate(beta);
      assert.equal(await page.locator('#detail-name').textContent(), 'Beta');
      assert.ok(!(await page.locator('#totals').textContent()).includes('样本 11'), 'Alpha statistics not shown under Beta');
      assert.ok(!(await page.locator('#events').textContent()).includes('target-a'), 'Alpha events cleared immediately');
      beta.release(); await textIncludes(page, '#totals', '样本 22');
      const alpha = mock.hold('GET', '/api/targets/target-a/timeline');
      await page.getByRole('button', { name: 'Alpha', exact: true }).click(); await waitForGate(alpha);
      await page.getByRole('button', { name: 'Beta', exact: true }).click(); await textIncludes(page, '#totals', '样本 22');
      alpha.release(); await sleep(100);
      assert.equal(await page.locator('#detail-name').textContent(), 'Beta');
      assert.ok((await page.locator('#totals').textContent()).includes('样本 22'));
      assert.equal(await page.inputValue('#protocol'), 'Tcp');
    });

    await test('newer protocol and time range reject stale detail', async ({ page, mock }) => {
      await page.getByRole('button', { name: 'Alpha', exact: true }).click(); await textIncludes(page, '#totals', '样本 11');
      const old = mock.hold('GET', '/api/targets/target-a/timeline', [200, timeline('target-a', 99)]);
      await page.selectOption('#days', '7'); await waitForGate(old);
      await page.selectOption('#protocol', 'Tcp');
      await eventually(() => mock.requests.some(r => r.url.includes('protocol=Tcp&days=7')), 'Latest detail query');
      await textIncludes(page, '#totals', '样本 11'); old.release(); await sleep(100);
      assert.ok(!(await page.locator('#totals').textContent()).includes('样本 99'), 'Older detail response ignored');
    }, { ignoreAbort: true });

    await test('logout rejects delayed overview and clears content', async ({ page, mock }) => {
      const oldState = clone(mock.state); oldState.site.name = 'STALE PRIVATE SITE';
      const old = mock.hold('GET', '/api/overview', [200, oldState]);
      await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange'))); await waitForGate(old);
      await page.locator('#logout').click(); await visible(page, '#login');
      old.release(); await sleep(100);
      await hidden(page, '#workspace'); assert.equal(await page.locator('#targets').textContent(), '');
      assert.notEqual(await page.locator('#site-name').textContent(), 'STALE PRIVATE SITE');
    }, { ignoreAbort: true });

    await test('stale unauthorized response cannot sign out a newer login', async ({ page, mock }) => {
      const old = mock.hold('GET', '/api/overview', [401, { error: 'Old request expired' }]);
      await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange'))); await waitForGate(old);
      await page.locator('#logout').click(); await visible(page, '#login');
      await page.fill('#password', password); await page.locator('#login-form button').click();
      await visible(page, '#workspace'); await textIncludes(page, '#target-count', '2 / 20');
      old.release(); await sleep(100); await visible(page, '#workspace'); await hidden(page, '#login');
    }, { ignoreAbort: true });

    await test('persisted back-forward restore rechecks auth and restarts polling', async ({ page, mock }) => {
      const sessions = mock.count('GET', '/api/auth/session');
      await restore(page);
      await eventually(() => mock.count('GET', '/api/auth/session') > sessions, 'Restore revalidates session');
      await visible(page, '#workspace');
      const overviews = mock.count('GET', '/api/overview');
      await eventually(() => mock.count('GET', '/api/overview') > overviews + 1, 'Polling resumes after restore', 6500);
      mock.authenticated = false; const rechecks = mock.count('GET', '/api/auth/session');
      await restore(page); await eventually(() => mock.count('GET', '/api/auth/session') > rechecks, 'Expired session rechecked');
      await visible(page, '#login'); await hidden(page, '#workspace');
      assert.equal(await page.locator('#targets').textContent(), '');
    });

    await test('actual away and back navigation restores usable UI', async ({ page }) => {
      await page.getByRole('button', { name: 'Beta', exact: true }).click(); await textIncludes(page, '#totals', '样本 22');
      await page.goto(base + '/away'); await page.goBack();
      await visible(page, '#workspace'); await textIncludes(page, '#target-count', '2 / 20');
      await page.getByRole('button', { name: 'Alpha', exact: true }).click(); await textIncludes(page, '#totals', '样本 11');
      await page.goForward(); assert.ok(page.url().endsWith('/away'));
      await page.goBack(); await visible(page, '#workspace'); await textIncludes(page, '#target-count', '2 / 20');
    });

    await test('mobile 390px layout, timeline, raw statuses and literal labels', async ({ page, mock }) => {
      mock.state.targets[0].profile.name = '<svg onload=alert(1)>';
      await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
      await visible(page, '#workspace');
      await page.getByRole('button', { name: '<svg onload=alert(1)>', exact: true }).click();
      await textIncludes(page, '#totals', '样本 11');
      assert.equal(await page.locator('#targets svg').count(), 0, 'No untrusted SVG element');
      assert.equal(await page.locator('#chart path.mean').count(), 1);
      assert.equal(await page.locator('#chart circle.failure').count(), 1);
      assert.equal(await page.locator('#hours .hour').count(), 2);
      assert.equal(await page.locator('#hours .hour:not(.good):not(.warn):not(.bad)').count(), 1, 'Local error / no samples remains neutral');
      assert.equal(await page.locator('#route-hops tr').count(), 3);
      assert.ok((await page.locator('#route-hops tr').nth(1).textContent()).includes('1 / 0'), 'Timeout remains separate from local error');
      assert.ok((await page.locator('#route-hops tr').nth(2).textContent()).includes('127.0.0.2'), 'TTL-expired hop is a reply');
      const layout = await page.evaluate(() => ({ width: innerWidth, body: document.documentElement.scrollWidth }));
      assert.ok(layout.body <= layout.width + 1, `No page-level overflow: ${JSON.stringify(layout)}`);
      await page.locator('#settings summary').click();
      const columns = await page.locator('#settings-form').evaluate(el => getComputedStyle(el).gridTemplateColumns.split(' ').length);
      assert.equal(columns, 1, 'Narrow settings uses one column');
      if (options.screenshots) {
        fs.mkdirSync(options.screenshots, { recursive: true });
        await page.screenshot({ path: path.join(options.screenshots, 'mobile-390.png'), fullPage: true });
      }
    }, { viewport: { width: 390, height: 844 } });

    if (options.url) {
      const context = await browser.newContext(); const page = await context.newPage(); page.setDefaultTimeout(10000);
      try {
        const realPassword = fs.readFileSync(options['password-file'], 'utf8').replace(/[\r\n]+$/, '');
        await page.goto(options.url); await visible(page, '#login');
        await page.fill('#password', realPassword); await page.locator('#login-form button').click();
        await visible(page, '#workspace'); await page.locator('#target-count').filter({ hasText: '/ 20' }).waitFor();
        const session = (await context.cookies()).find(cookie => cookie.name === 'iqm.session');
        assert.ok(session?.httpOnly, 'Real auth cookie is HttpOnly'); assert.equal(session.sameSite, 'Strict');
        await page.reload(); await visible(page, '#workspace');
        await page.locator('#logout').click(); await visible(page, '#login');
        assert.equal((await context.request.get(options.url.replace(/\/$/, '') + '/api/overview')).status(), 401);
        await page.reload(); await visible(page, '#login'); await hidden(page, '#workspace');
        console.log('PASS real ASP.NET login, cookie, reload, logout and protected API rejection'); passed++;
      } catch (error) { console.error('FAIL real ASP.NET authentication: ' + error.message); failures++; }
      finally { await context.close(); }
    } else console.log('SKIP real ASP.NET authentication (supply --url and --password-file)');
    console.log(`${passed} passed; ${failures} failed`);
    if (failures) process.exitCode = 1;
  } finally {
    await browser?.close(); await new Promise(resolve => server.close(resolve));
  }
}
main().catch(error => { console.error(error.stack || error.message); process.exitCode = 1; });
