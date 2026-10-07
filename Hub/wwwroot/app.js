'use strict';

const $ = (sel, el = document) => el.querySelector(sel);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const short = sha => (sha || '').slice(0, 7);

let overview = null;
let dbs = null;
let editing = null;      // deployment name being edited, null when creating
let logView = null;      // { name, kind, timer }
let refreshTimer = null;

async function api(path, options = {}) {
  const headers = { 'X-Portable-Hub': '1', ...(options.body ? { 'Content-Type': 'application/json' } : {}) };
  const res = await fetch(path, { ...options, headers });
  const text = await res.text();
  if (!res.ok) {
    let message = text;
    try { const j = JSON.parse(text); message = j.detail || j.title || text; } catch { }
    throw new Error(message || `${res.status} ${res.statusText}`);
  }
  try { return JSON.parse(text); } catch { return text; }
}

function ago(iso) {
  if (!iso) return 'never';
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  if (s < 86400 * 30) return `${Math.floor(s / 86400)} d ago`;
  return new Date(iso).toISOString().slice(0, 10);
}

const size = b => b >= 1e9 ? `${(b / 1e9).toFixed(2)} GB` : `${Math.round(b / 1e6)} MB`;
const slug = s => s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 32).replace(/-+$/, '') || 'app';

function toast(message, bad = false) {
  const t = $('#toast');
  t.textContent = message;
  t.style.background = bad ? 'var(--bad)' : '';
  t.classList.add('show');
  clearTimeout(t._timer);
  t._timer = setTimeout(() => t.classList.remove('show'), bad ? 6000 : 2500);
}

async function load() {
  try {
    overview = await api('/api/overview');
    render();
  } catch (e) {
    $('#deployments').innerHTML = `<p class="alert">Cannot reach the hub API: ${esc(e.message)}</p>`;
  }
  clearTimeout(refreshTimer);
  const busy = overview?.deployments.some(d => d.activity);
  refreshTimer = setTimeout(load, busy ? 2000 : 6000);
}

function render() {
  renderChips();
  renderDeployments();
  renderBranches();
  renderSetup();
}

function chip(state, text, title = '') {
  return `<span class="chip ${state}" title="${esc(title)}"><span class="dot"></span>${esc(text)}</span>`;
}

function renderChips() {
  const h = overview.hub;
  const cf = h.cloudflare;
  $('#chips').innerHTML = [
    cf.configured ? chip(cf.status === 'ok' || cf.status === 'not checked' ? 'ok' : 'bad', `Cloudflare: ${cf.status === 'ok' ? 'ok' : cf.status}`, cf.status)
      : chip('warn', 'Cloudflare: no token', 'DNS records are not created automatically'),
    h.lastFetchError ? chip('bad', 'GitHub: fetch failed', h.lastFetchError) : chip('ok', `branches fetched ${ago(h.lastFetch)}`),
    h.lastWebhook ? chip('ok', `GitHub webhook ${ago(h.lastWebhook)}`, 'last signed delivery') : chip('', `polling every ${h.pollSeconds}s`),
    chip(h.diskFreeGb < 5 ? 'bad' : h.diskFreeGb < 12 ? 'warn' : 'ok', `${h.diskFreeGb} GB disk free`, `of ${h.diskTotalGb} GB`),
    chip(h.memAvailableMb < 400 ? 'bad' : h.memAvailableMb < 900 ? 'warn' : 'ok', `${h.memAvailableMb} MB RAM free`, `of ${h.memTotalMb} MB; ${h.swapFreeMb} MB swap free`),
  ].join('');
}

function statusBadge(d) {
  if (d.activity) return `<span class="badge info">deploying</span>`;
  const cls = { Running: 'ok', Failed: 'bad', Stopped: '', Building: 'info', Queued: 'info' }[d.status] ?? '';
  return `<span class="badge ${cls}">${esc(d.status.toLowerCase())}</span>`;
}

function liveText(d) {
  const u = d.unit;
  if (!u || u.activeState === 'unknown') return '<span class="muted">no service</span>';
  const mem = u.memoryMb != null ? ` · ${u.memoryMb} MB` : '';
  const cls = u.activeState === 'active' ? '' : 'muted';
  return `<span class="${cls}">${esc(u.activeState)}${u.subState ? ` (${esc(u.subState)})` : ''}${mem}</span>`;
}

function renderDeployments() {
  const list = overview.deployments;
  const repo = overview.hub.repoWebUrl;
  $('#deploy-count').textContent = list.length ? `${list.length} running on this box` : '';
  if (!list.length) {
    $('#deployments').innerHTML = `<div class="card"><p class="muted">No deployments yet. Pick a branch below or press <b>New deployment</b>.</p></div>`;
    return;
  }
  $('#deployments').innerHTML = list.map(d => {
    const behind = d.branchTip && d.commit && d.branchTip !== d.commit;
    const skipped = behind && d.skippedCommit === d.branchTip;
    const commit = d.commit
      ? `<a class="mono" href="${esc(repo)}/commit/${esc(d.commit)}" target="_blank" rel="noopener">${short(d.commit)}</a> ${esc(d.commitSubject)}`
      : '<span class="muted">not deployed yet</span>';
    const update = skipped
      ? ` <span class="badge" title="newer commits up to ${esc(d.branchTip)} only change ignored paths (${d.skippedFiles} files)">up to date with ${short(d.branchTip)}</span>`
      : behind
        ? ` <span class="badge warn" title="branch is at ${esc(d.branchTip)}">${d.autoDeploy ? 'update pending' : 'update available'} ${short(d.branchTip)}</span>` : '';
    const db = d.activeDbKey || d.dbKey;
    const dbText = db ? `<code>${esc(db)}</code>` : '<span class="muted">branch default</span>';
    const dbPending = d.dbKey && d.activeDbKey && d.dbKey !== d.activeDbKey ? ` → <code>${esc(d.dbKey)}</code>` : '';
    const cmp = d.comparisonKey ? `<code>${esc(d.comparisonKey)}</code>` : '<span class="muted">none</span>';
    const running = d.unit?.activeState === 'active' || d.unit?.activeState === 'activating';
    return `
      <article class="card" data-name="${esc(d.name)}">
        <div class="card-top">
          <div class="card-title">
            <h3>${esc(d.name)}</h3>
            ${statusBadge(d)}
            <a href="${esc(d.url)}" target="_blank" rel="noopener">${esc(d.host)}</a>
            ${d.protected ? '<span class="badge" title="requires the hub login">login</span>' : ''}
            ${d.autoDeploy ? '' : '<span class="badge" title="pushes are not deployed automatically">manual</span>'}
          </div>
          <div class="card-actions">
            <button class="btn small" data-act="logs">Logs</button>
            <button class="btn small" data-act="deploy" ${d.activity ? 'disabled' : ''}>Redeploy</button>
            <button class="btn small" data-act="restart" ${running && !d.activity ? '' : 'disabled'}>Restart</button>
            ${d.status === 'Stopped'
              ? '<button class="btn small" data-act="start">Start</button>'
              : `<button class="btn small" data-act="stop" ${d.activity ? 'disabled' : ''}>Stop</button>`}
            <button class="btn small" data-act="edit">Edit</button>
            <button class="btn small danger" data-act="delete" ${d.activity?.startsWith('deleting') ? 'disabled' : ''}>Delete</button>
          </div>
        </div>
        ${d.notes ? `<div class="notes-line">${esc(d.notes)}</div>` : ''}
        ${d.activity ? `<div class="activity"><span class="spinner"></span>${esc(d.activity)}</div>` : ''}
        ${d.error ? `<div class="alert">${esc(d.error)}</div>` : ''}
        <div class="facts">
          <div class="fact"><div class="label">Branch</div><div class="value"><a href="${esc(repo)}/tree/${encodeURIComponent(d.branch)}" target="_blank" rel="noopener">${esc(d.branch)}</a></div></div>
          <div class="fact"><div class="label">Commit</div><div class="value">${commit}${update}</div></div>
          <div class="fact"><div class="label">Database</div><div class="value">${dbText}${dbPending}</div></div>
          <div class="fact"><div class="label">Comparison</div><div class="value">${cmp}</div></div>
          <div class="fact"><div class="label">Service</div><div class="value">${liveText(d)}</div></div>
          <div class="fact"><div class="label">Deployed</div><div class="value">${d.deployedAt ? ago(d.deployedAt) : '—'}${d.args ? ` · <code>${esc(d.args)}</code>` : ''}</div></div>
        </div>
      </article>`;
  }).join('');
}

function renderBranches() {
  const filter = $('#branch-filter').value.trim().toLowerCase();
  const repo = overview.hub.repoWebUrl;
  const rows = overview.branches.filter(b => !filter || b.name.toLowerCase().includes(filter) || b.subject.toLowerCase().includes(filter));
  $('#branches').innerHTML = rows.map(b => `
    <tr>
      <td><a href="${esc(repo)}/tree/${encodeURIComponent(b.name)}" target="_blank" rel="noopener"><b>${esc(b.name)}</b></a></td>
      <td class="subject"><a class="mono" href="${esc(repo)}/commit/${esc(b.sha)}" target="_blank" rel="noopener">${short(b.sha)}</a>
        ${esc(b.subject)}<br><span class="muted">${esc(b.author)} · ${ago(b.date)}</span></td>
      <td>${b.deployments.map(n => `<span class="badge">${esc(n)}</span>`).join(' ') || '<span class="muted">—</span>'}</td>
      <td class="right"><button class="btn small" data-branch="${esc(b.name)}">Deploy…</button></td>
    </tr>`).join('') || `<tr><td colspan="4" class="muted">${overview.branches.length ? 'No branch matches.' : 'Fetching branches…'}</td></tr>`;
}

function renderSetup() {
  const h = overview.hub;
  const items = [];
  if (!h.cloudflare.configured)
    items.push(`No Cloudflare token: DNS records for new deployments are not created. Put a token with <i>Zone · DNS · Edit</i> for <b>${esc(h.zone)}</b> into <code>/etc/portable-hub/cloudflare.token</code> (owned by <code>portable</code>, mode 600) — no restart needed.`);
  if (!h.webhook)
    items.push(`Pushes are picked up by polling every ${h.pollSeconds}s. For instant redeploys set <code>GitHubWebhookSecret</code> in <code>/etc/portable-hub/hub.json</code>, restart the hub and add a GitHub webhook: <code>${esc(h.webhookUrl)}</code>, content type <i>application/json</i>, push events.`);
  else if (!h.lastWebhook)
    items.push(`Pushes are picked up by polling every ${h.pollSeconds}s. For instant redeploys add a GitHub webhook (repository → Settings → Webhooks): payload URL <code>${esc(h.webhookUrl)}</code>, content type <i>application/json</i>, secret from <code>jq -r .GitHubWebhookSecret /etc/portable-hub/hub.json</code>, just the push event.`);
  $('#setup-notes').hidden = !items.length;
  $('#setup-body').innerHTML = `<ul>${items.map(i => `<li>${i}</li>`).join('')}</ul>`;
}

// ---- deploy / edit form
async function openForm(deployment = null, branch = null) {
  editing = deployment?.name ?? null;
  const f = $('#deploy-form');
  f.reset();
  $('#form-error').textContent = '';
  $('#form-title').textContent = editing ? `Edit ${editing}` : 'New deployment';
  $('#form-submit').textContent = editing ? 'Save' : 'Deploy';
  f.depname.disabled = !!editing;
  f.branch.innerHTML = overview.branches.map(b => `<option value="${esc(b.name)}">${esc(b.name)}</option>`).join('');
  const selectedBranch = deployment?.branch ?? branch ?? overview.branches[0]?.name ?? '';
  f.branch.value = selectedBranch;
  f.depname.value = deployment?.name ?? (branch ? slug(branch) : '');
  f.args.value = deployment?.args ?? '';
  f.notes.value = deployment?.notes ?? '';
  f.autoDeploy.checked = deployment?.autoDeploy ?? true;
  f.protected.checked = deployment?.protected ?? false;
  updateHostPreview();
  fillDbSelects(deployment);
  $('#form-dialog').showModal();
  (editing ? f.branch : f.depname).focus();
  try {
    dbs = await api('/api/dbs');
    fillDbSelects(deployment);
  } catch (e) {
    $('#form-error').textContent = `Cannot list S3 databases: ${e.message}`;
  }
}

function fillDbSelects(deployment) {
  const f = $('#deploy-form');
  const options = (dbs ?? []).map(o => `<option value="${esc(o.key)}">${esc(o.key)} — ${size(o.size)}, ${new Date(o.modified).toISOString().slice(0, 10)}</option>`).join('');
  const keep = (select, value, first) => {
    const known = !value || (dbs ?? []).some(o => o.key === value);
    select.innerHTML = first + (known ? '' : `<option value="${esc(value)}">${esc(value)}</option>`) + options;
    select.value = value ?? '';
  };
  keep(f.dbKey, deployment?.dbKey, `<option value="">Branch default (wwwroot/current_db_name.txt)</option>`);
  keep(f.comparisonKey, deployment?.comparisonKey, `<option value="">None</option>`);
}

function updateHostPreview() {
  const name = $('#deploy-form').depname.value || 'name';
  $('#host-preview').textContent = `→ https://${overview.hub.hostPattern.replace('{name}', name)}`;
}

$('#deploy-form').addEventListener('submit', async e => {
  e.preventDefault();
  const f = e.target;
  const body = {
    name: f.depname.value.trim(),
    branch: f.branch.value,
    dbKey: f.dbKey.value,
    comparisonKey: f.comparisonKey.value,
    args: f.args.value,
    notes: f.notes.value,
    autoDeploy: f.autoDeploy.checked,
    protected: f.protected.checked,
  };
  $('#form-submit').disabled = true;
  try {
    if (editing) await api(`/api/deployments/${encodeURIComponent(editing)}`, { method: 'PUT', body: JSON.stringify(body) });
    else await api('/api/deployments', { method: 'POST', body: JSON.stringify(body) });
    $('#form-dialog').close();
    toast(editing ? 'Saved' : `Deploying ${body.name}…`);
    load();
  } catch (err) {
    $('#form-error').textContent = err.message;
  } finally {
    $('#form-submit').disabled = false;
  }
});
$('#form-cancel').addEventListener('click', () => $('#form-dialog').close());
$('#deploy-form').depname.addEventListener('input', updateHostPreview);

// ---- logs
async function showLog() {
  if (!logView) return;
  const pre = $('#log-body');
  const atBottom = pre.scrollTop + pre.clientHeight >= pre.scrollHeight - 30;
  try {
    pre.textContent = await api(`/api/deployments/${encodeURIComponent(logView.name)}/log?kind=${logView.kind}&lines=1500`) || '(empty)';
  } catch (e) {
    pre.textContent = `Cannot load the log: ${e.message}`;
  }
  if (atBottom) pre.scrollTop = pre.scrollHeight;
  logView.timer = setTimeout(showLog, 2000);
}

function openLogs(name, kind = 'deploy') {
  closeLogs();
  logView = { name, kind };
  $('#log-title').textContent = `${name} — logs`;
  document.querySelectorAll('#log-dialog .tab').forEach(t => t.classList.toggle('active', t.dataset.kind === kind));
  $('#log-body').textContent = 'Loading…';
  $('#log-dialog').showModal();
  showLog().then(() => { const pre = $('#log-body'); pre.scrollTop = pre.scrollHeight; });
}

function closeLogs() {
  if (logView) clearTimeout(logView.timer);
  logView = null;
}

$('#log-dialog').addEventListener('close', closeLogs);
$('#log-close').addEventListener('click', () => $('#log-dialog').close());
document.querySelectorAll('#log-dialog .tab').forEach(t => t.addEventListener('click', () => openLogs(logView.name, t.dataset.kind)));

// ---- actions
$('#deployments').addEventListener('click', async e => {
  const button = e.target.closest('button[data-act]');
  if (!button) return;
  const name = button.closest('[data-name]').dataset.name;
  const d = overview.deployments.find(x => x.name === name);
  const act = button.dataset.act;
  if (act === 'logs') return openLogs(name, d.activity || d.status !== 'Running' ? 'deploy' : 'app');
  if (act === 'edit') return openForm(d);
  if (act === 'delete' && !confirm(`Delete ${name}? This stops it, removes its files and its DNS record.`)) return;
  if (act === 'stop' && !confirm(`Stop ${name}? It stays stopped (also after reboots) until you start it.`)) return;
  button.disabled = true;
  try {
    if (act === 'delete') await api(`/api/deployments/${encodeURIComponent(name)}`, { method: 'DELETE' });
    else await api(`/api/deployments/${encodeURIComponent(name)}/${act}`, { method: 'POST' });
    toast({ deploy: 'Redeploy queued', delete: 'Deleting…', stop: 'Stopped', start: 'Starting…', restart: 'Restarting…' }[act]);
    load();
  } catch (err) {
    toast(err.message, true);
    button.disabled = false;
  }
});

$('#branches').addEventListener('click', e => {
  const button = e.target.closest('button[data-branch]');
  if (button) openForm(null, button.dataset.branch);
});
$('#branch-filter').addEventListener('input', () => overview && renderBranches());
$('#new-btn').addEventListener('click', () => overview && openForm());
$('#refresh-btn').addEventListener('click', async () => {
  try { await api('/api/refresh', { method: 'POST' }); toast('Fetching branches…'); setTimeout(load, 3000); }
  catch (e) { toast(e.message, true); }
});

load();
