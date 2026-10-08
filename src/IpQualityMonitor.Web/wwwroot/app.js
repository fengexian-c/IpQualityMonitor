const $ = id => document.getElementById(id);
let csrf = '', snapshot = null, selected = localStorage.getItem('iqm.target') || '', overviewBusy = false;
let overviewTimer = null, detailTimer = null, detailController = null, routes = [];
const message = text => { $('message').textContent = text || ''; };
async function api(path, options = {}) {
  const { body, signal, ...rest } = options;
  const headers = { ...(options.headers || {}) };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (rest.method && rest.method !== 'GET') headers['X-CSRF-TOKEN'] = csrf;
  const response = await fetch('/api' + path, { ...rest, headers, signal: signal || AbortSignal.timeout(15000),
    credentials: 'same-origin', body: body === undefined ? undefined : JSON.stringify(body) });
  if (response.status === 401) { showLogin(); throw new Error('请登录或重新登录。'); }
  if (!response.ok) { const data = await response.json().catch(() => ({})); throw new Error(data.error || `请求失败（${response.status}）`); }
  if (response.status === 204 || response.headers.get('content-length') === '0') return null;
  return response.json().catch(() => null);
}
async function getCsrf() { csrf = (await api('/auth/csrf')).token; }
function stopPolling() { clearInterval(overviewTimer); clearInterval(detailTimer); detailController?.abort(); }
function showLogin() { stopPolling(); $('login').hidden = false; $('workspace').hidden = true; $('logout').hidden = true; }
async function showWorkspace() {
  await getCsrf(); $('login').hidden = true; $('workspace').hidden = false; $('logout').hidden = false;
  stopPolling(); await refreshOverview();
  overviewTimer = setInterval(() => { if (!document.hidden) refreshOverview(); }, 2000);
  detailTimer = setInterval(() => { if (!document.hidden) refreshDetail(); }, 15000);
  await refreshDetail();
}
const format = (date) => new Intl.DateTimeFormat('zh-CN', { dateStyle: 'short', timeStyle: 'medium', timeZone: snapshot?.timeZone || 'Asia/Shanghai' }).format(new Date(date));
const number = value => Number.isFinite(value) ? value.toFixed(2) : '—';
const stateLabels = { Success: '成功', Refused: '连接被拒绝', Timeout: '超时', Unreachable: '不可达', LocalError: '本地错误', Failed: '失败' };
function cell(row, text) { const el = document.createElement('td'); el.textContent = text; row.append(el); return el; }
function button(text, action, className = '') { const el = document.createElement('button'); el.textContent = text; el.className = className;
  el.addEventListener('click', async () => { el.disabled = true; try { await action(); message(''); } catch (e) { message(e.message); } finally { el.disabled = false; } }); return el; }
async function refreshOverview() {
  if (overviewBusy || $('workspace').hidden) return;
  overviewBusy = true;
  try {
    snapshot = await api('/overview');
    $('site-name').textContent = snapshot.site.name;
    $('site-info').textContent = `采集点 ${snapshot.site.id.slice(0, 8)} · ${snapshot.timeZone} · ${format(snapshot.serverTime)}`;
    const errors = [snapshot.error, snapshot.analysisError, snapshot.probeError].filter(Boolean);
    $('health').hidden = !errors.length; $('health').textContent = errors.join('；');
    $('target-count').textContent = `${snapshot.targets.length} / 20 个目标`;
    $('empty').hidden = snapshot.targets.length > 0;
    if (selected && !snapshot.targets.some(x => x.profile.id === selected)) { selected = ''; $('detail').hidden = true; }
    const fragment = document.createDocumentFragment();
    for (const target of snapshot.targets) {
      const p = target.profile, row = document.createElement('tr'); if (p.id === selected) row.className = 'selected';
      const name = cell(row, ''); name.append(button(p.name || p.address, () => select(p.id), 'link'));
      const address = document.createElement('small'); address.textContent = p.address + (p.mode !== 'Icmp' ? ` : ${p.port}` : ''); name.append(address);
      cell(row, p.mode); const states = [target.primary, target.secondary].filter(Boolean);
      const statusCell = cell(row, !target.running ? (p.resumeOnLaunch ? '期望运行／实际已停止' : '已暂停') : states.map(s => `${s.target.protocol}: ${stateLabels[s.last?.status] || s.health}`).join(' · '));
      statusCell.title = states.map(s => s.last?.detail || '').filter(Boolean).join('\n');
      cell(row, states.map(s => `${s.target.protocol}: ${number(s.last?.latencyMs)} ms`).join(' · ') || '—');
      const actions = document.createElement('div'); actions.className = 'actions'; cell(row, '').append(actions);
      actions.append(button(target.running ? '暂停' : '开始', async () => {
        await api(`/targets/${p.id}/run`, { method: 'POST', body: { revision: snapshot.config.revision, running: !target.running } }); await refreshOverview();
      }, 'secondary'));
      actions.append(button('移除', async () => {
        if (!confirm('移除目标配置？保留期限内的原始历史不会因此被删除。')) return;
        await api(`/targets/${p.id}?revision=${snapshot.config.revision}`, { method: 'DELETE' }); await refreshOverview();
      }, 'danger'));
      fragment.append(row);
    }
    $('targets').replaceChildren(fragment);
    const selectedProfile = snapshot.targets.find(x => x.profile.id === selected)?.profile;
    if (selectedProfile) {
      if (selectedProfile.mode !== 'Both') $('protocol').value = selectedProfile.mode;
      for (const option of $('protocol').options)
        option.disabled = selectedProfile.mode !== 'Both' && option.value !== selectedProfile.mode;
    }
    if (!$('settings').open) populateSettings();
  } catch (e) { if (e.name !== 'AbortError') message(e.message); }
  finally { overviewBusy = false; }
}
function populateSettings() {
  if (!snapshot) return;
  const form = $('settings-form'); form.dataset.revision = snapshot.config.revision;
  for (const [key, value] of Object.entries(snapshot.config.monitoring)) {
    const el = form.elements.namedItem(key); if (!el) continue;
    if (el.type === 'checkbox') el.checked = value; else el.value = value;
  }
}
async function select(id) {
  selected = id; localStorage.setItem('iqm.target', id);
  const p = snapshot.targets.find(x => x.profile.id === id).profile;
  $('protocol').value = p.mode === 'Tcp' ? 'Tcp' : 'Icmp';
  for (const option of $('protocol').options) option.disabled = p.mode !== 'Both' && option.value !== p.mode;
  $('detail').hidden = false; $('detail-name').textContent = p.name || p.address;
  await refreshDetail();
}
function svgElement(name, attrs) { const el = document.createElementNS('http://www.w3.org/2000/svg', name);
  for (const [k,v] of Object.entries(attrs)) el.setAttribute(k, v); return el; }
function drawTimeline(t) {
  const chart = $('chart'); chart.replaceChildren();
  const max = Math.max(1, ...t.points.map(p => p.maximum || 0));
  const begin = Date.parse(t.from), duration = Math.max(1, Date.parse(t.until) - begin);
  let means = '', ranges = '', connected = false;
  for (const p of t.points) {
    const x = 45 + 900 * (Date.parse(p.start) - begin) / duration;
    if (Number.isFinite(p.average)) {
      const y = 175 - p.average / max * 145;
      means += `${connected ? 'L' : 'M'}${x.toFixed(2)} ${y.toFixed(2)} `; connected = true;
      if (Number.isFinite(p.minimum) && Number.isFinite(p.maximum)) ranges += `M${x} ${175-p.minimum/max*145}L${x} ${175-p.maximum/max*145} `;
    } else connected = false;
    if (p.attempts > p.successes) chart.append(svgElement('circle', { cx: x, cy: 190, r: 2.5, class: 'failure' }));
  }
  chart.append(svgElement('path', { d: ranges, class: 'range' }), svgElement('path', { d: means, class: 'mean' }));
  for (const [y,label] of [[22,`${number(max)} ms`],[175,'0']]) { const text = svgElement('text', { x: 7, y }); text.textContent = label; chart.append(text); }
  $('totals').textContent = `样本 ${t.total.attempts} · 成功 ${t.total.successes} · 本地错误 ${t.total.localErrors} · 成功率 ${number(t.total.availability)}% · 平均 ${number(t.total.average)} ms`;
  $('window').textContent = `${format(t.from)} — ${format(t.until)} · 每 ${t.stepMinutes} 分钟汇总；小时格来自同一读取快照`;
  const boxes = document.createDocumentFragment();
  for (const h of t.hours) {
    const el = document.createElement('span'); const ratio = h.availability;
    el.className = 'hour' + (ratio === null ? '' : ratio >= 99 ? ' good' : ratio >= 90 ? ' warn' : ' bad');
    el.title = `${format(h.start)}\n成功 ${h.successes}/${h.attempts}；本地错误 ${h.localErrors}；平均 ${number(h.average)} ms`;
    el.setAttribute('aria-label', el.title); boxes.append(el);
  }
  $('hours').replaceChildren(boxes);
}
async function refreshDetail() {
  if (!selected || $('workspace').hidden || document.hidden) return;
  detailController?.abort(); detailController = new AbortController();
  const signal = AbortSignal.any([detailController.signal, AbortSignal.timeout(15000)]), id = selected;
  try {
    const [data, routeData, events] = await Promise.all([
      api(`/targets/${id}/timeline?protocol=${$('protocol').value}&days=${$('days').value}`, { signal }),
      api(`/targets/${id}/routes`, { signal }), api(`/targets/${id}/events`, { signal })]);
    if (id !== selected || signal.aborted) return;
    $('detail').hidden = false; $('detail-name').textContent = snapshot.targets.find(x => x.profile.id === id)?.profile.name || snapshot.targets.find(x => x.profile.id === id)?.profile.address || id;
    drawTimeline(data.timeline); const previous = $('route-list').value; routes = routeData;
    $('route-list').replaceChildren(...routes.map(r => { const el = document.createElement('option'); el.value = r.id; el.textContent = `${format(r.started)} · ${r.outcome}`; return el; }));
    if (routes.some(r => r.id === previous)) $('route-list').value = previous;
    showRoute(); $('events').replaceChildren(...events.map(e => { const p = document.createElement('p'); p.textContent = `${format(e.time)} · ${e.kind} · ${e.detail}`; return p; }));
  } catch (e) { if (e.name !== 'AbortError' && !signal.aborted) message(e.message); }
}
function showRoute() {
  const run = routes.find(r => r.id === $('route-list').value); $('route-hops').replaceChildren();
  if (!run) { $('route-info').textContent = '尚无路由。启用路由并开始监测后，结果将自动保存。'; return; }
  $('route-info').textContent = `${run.contextDescription || run.context} · ${run.outcome} · ${run.supplementOutcome || ''}`;
  const groups = new Map(); for (const p of run.probes) { if (!groups.has(p.ttl)) groups.set(p.ttl, []); groups.get(p.ttl).push(p); }
  for (const [ttl, probes] of [...groups].sort((a,b) => a[0]-b[0])) {
    const replies = probes.filter(p => p.status === 0 || p.status === 11013), timeouts = probes.filter(p => p.status === 11010).length;
    const row = document.createElement('tr'); cell(row, ttl); cell(row, [...new Set(replies.map(p => p.address))].join(' / ') || '—');
    cell(row, `${replies.length}/${probes.length}`); cell(row, `${timeouts} / ${probes.length-timeouts-replies.length}`);
    const rtts = replies.map(p => p.rttMs).filter(Number.isFinite); cell(row, number(rtts.length ? rtts.reduce((a,b) => a+b,0)/rtts.length : null) + ' ms'); $('route-hops').append(row);
  }
}
$('login-form').addEventListener('submit', async event => { event.preventDefault(); const submit = event.target.querySelector('button'); submit.disabled = true;
  try { await getCsrf(); await api('/auth/login', { method: 'POST', body: { password: $('password').value } }); $('password').value = ''; message(''); await showWorkspace(); }
  catch (e) { message(e.message); } finally { submit.disabled = false; } });
$('logout').addEventListener('click', async () => { try { await api('/auth/logout', { method: 'POST' }); showLogin(); } catch (e) { message(e.message); } });
$('add-form').addEventListener('submit', async event => { event.preventDefault(); const form = event.target, data = new FormData(form);
  try { await api('/targets', { method: 'POST', body: { revision: snapshot.config.revision, target: { name: data.get('name'), address: data.get('address'), mode: data.get('mode'), port: Number(data.get('port')) } } }); form.reset(); message('目标已添加，尚未开始采集。'); await refreshOverview(); }
  catch (e) { message(e.message); } });
$('settings').addEventListener('toggle', () => { if ($('settings').open) populateSettings(); });
$('settings-form').addEventListener('submit', async event => { event.preventDefault(); const form = event.target, values = {};
  for (const el of form.elements) if (el.name) values[el.name] = el.type === 'checkbox' ? el.checked : Number(el.value);
  try { await api('/settings', { method: 'PUT', body: { revision: Number(form.dataset.revision), monitoring: values } }); $('settings').open = false; message('已保存并应用统一设置。'); await refreshOverview(); }
  catch (e) { message(e.message); } });
$('days').addEventListener('change', refreshDetail); $('protocol').addEventListener('change', refreshDetail); $('route-list').addEventListener('change', showRoute);
$('probe-route').addEventListener('click', async () => { try { await api(`/targets/${selected}/route`, { method: 'POST' }); message('路由检查请求已接受，结果将在后台保存。'); } catch (e) { message(e.message); } });
document.addEventListener('visibilitychange', () => { if (document.hidden) detailController?.abort(); else { refreshOverview(); refreshDetail(); } });
window.addEventListener('pagehide', stopPolling);
try { const session = await api('/auth/session'); if (session.authenticated) await showWorkspace(); else showLogin(); } catch (e) { message(e.message); showLogin(); }
