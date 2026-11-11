// Claude analysis: status, plan usage, start/stop, and the live run (tool steps, notes, streamed summary).
import { h, clear, size, date, toast, copyText, pct, meter, skeleton, tip } from '../ui.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { store, cached, currentScan, jobRunning, pref } from '../store.js';
import { get, post, ApiError } from '../api.js';

let box, ctxRef, data, findingsById = new Map(), live, textEl, progressEl, stepsEl, toolCards = new Map(), scope = 'all';

export async function render(container, ctx) {
  box = container; ctxRef = ctx;
  scope = ctx.params.get('scope') || scope;
  box.appendChild(skeleton(5));
  await load();
}

async function load() {
  data = await get('/api/analysis');
  const scan = currentScan();
  if (scan) {
    try { const f = await cached('/api/findings?scan=' + scan.id); findingsById = new Map(f.findings.map(x => [x.id, x])); } catch { /* no findings */ }
  }
  if (!ctxRef.isCurrent()) return;
  draw();
}

export function onEvent(what, ev) {
  if (what === 'analysis' && ev) { data.events.push(ev); addEvent(ev, true); return; }
  if (what === 'job') {
    const j = store.state?.job;
    if (j?.kind === 'analysis' && !j.finishedAt && !data.running) { data.running = true; draw(); }
    else if (j?.kind === 'analysis' && j.finishedAt && data.running) { setTimeout(load, 300); }
  }
}

function draw() {
  clear(box);
  const c = data.claude;
  box.append(
    h('div.page-head', h('div', h('div.eyebrow', t('navAnalysis')), h('h1.page-title', t('anTitle'), tip(t('anLede'))))),
    h('div.grid.grid-hero', statusCard(c), usageCard(data.usage)));
  if (c.installed && c.loggedIn) box.appendChild(runCard());
  box.appendChild(h('div.note', { class: 'mt' }, icon('shield'), h('div', t('anPrivacy'))));
}

function statusCard(c) {
  if (!c.installed) {
    return h('div.card.card-pad',
      h('div.card-head', icon('robot'), h('h2', t('anNotInstalled'))),
      h('p.muted', t('anInstallText')),
      cmd('winget install Anthropic.ClaudeCode'),
      h('p.muted', { class: 'small-note' }, t('anThenLogin')),
      cmd('claude'),
      h('div.row-flex', { class: 'mt' }, h('button.btn', { onclick: refresh }, icon('undo'), t('anCheckAgain'))));
  }
  if (!c.loggedIn) {
    return h('div.card.card-pad',
      h('div.card-head', icon('robot'), h('h2', t('anNotLoggedIn'))),
      h('p.muted', t('anLoginText')), cmd('claude'),
      h('div.row-flex', { class: 'mt' }, h('button.btn', { onclick: refresh }, icon('undo'), t('anCheckAgain'))));
  }
  return h('div.card.card-pad',
    h('div.card-head', icon('robot'), h('h2', 'Claude Code'), h('span.chip.safe', t('anReady'))),
    h('dl.kv',
      h('dt', t('anVersion')), h('dd', c.version || '–'),
      h('dt', t('anAccount')), h('dd', [c.authMethod, c.subscriptionType].filter(Boolean).join(' · ') || '–')),
    c.apiKeyInEnvironment ? h('div.note.warn', { class: 'mt' }, icon('warn'), h('div', t('anApiKey'))) : null,
    h('p.muted', { class: 'small-note' }, t('anOwnInstall')));
}

const cmd = text => h('div.cmdline', h('code', text), h('button.btn.btn-sm', { text: t('copy'), onclick: e => copyText(text, e.currentTarget) }));

async function refresh() {
  data = await post('/api/analysis/refresh');
  draw();
}

function usageCard(u) {
  const win = (label, v, at) => v === undefined || v === null ? null : h('div.usage',
    h('div.row-flex', h('span', label), h('span.sp'), h('b.num', pct(v))),
    meter(v, v > 0.8 ? 'review' : 'accent'),
    at ? h('div.muted.small', t('anResets', date(new Date(at * 1000).toISOString()))) : null);
  return h('div.card.card-pad',
    h('div.card-head', icon('clock'), h('h2', t('anUsage'))),
    !u ? h('p.muted', t('anUsageUnknown')) : h('div.stack.tight',
      win(t('anFiveHour'), u.fiveHour, u.fiveHourResetsAt),
      win(t('anWeekly'), u.sevenDay, u.sevenDayResetsAt),
      u.status === 'allowed_warning' ? h('div.note.warn', icon('warn'), h('div', t('anUsageWarn'))) : null,
      h('p.muted.small', t('anUsageAt', date(u.at)))),
    h('p.muted', { class: 'small-note' }, t('anUsageNote')));
}

function runCard() {
  const scan = currentScan();
  const running = data.running || (jobRunning() && store.state.job.kind === 'analysis');
  const model = h('select.select', { 'aria-label': t('anModel') },
    [['', t('anModelDefault')], ['haiku', 'Haiku · ' + t('anCheapest')], ['sonnet', 'Sonnet'], ['opus', 'Opus']].map(([v, l]) => h('option', { value: v, text: l, selected: pref.get('model', '') === v })));
  model.addEventListener('change', () => pref.set('model', model.value));

  // What to cover: every suggestion to assess, only those without a note, or the quarantine before deleting.
  const toAssess = [...findingsById.values()].filter(f => f.action !== 'info' && (f.bytes >= 10 * 1048576 || f.risk === 'review'));
  const unnoted = toAssess.filter(f => !f.note).length;
  const inQuarantine = store.state?.quarantineItems || 0;
  const scopes = [['all', t('anScopeAll', toAssess.length)]];
  if (unnoted && unnoted < toAssess.length) scopes.push(['unnoted', t('anScopeUnnoted', unnoted)]);
  if (inQuarantine) scopes.push(['quarantine', t('anScopeQuarantine', inQuarantine)]);
  if (!scopes.some(([v]) => v === scope)) scope = 'all';
  const scopeSel = h('select.select', { 'aria-label': t('anScope') },
    scopes.map(([v, l]) => h('option', { value: v, text: l, selected: v === scope })));
  scopeSel.addEventListener('change', () => { scope = scopeSel.value; });

  const head = h('div.card-head', icon('sparkle'), h('h2', running ? t('anRunning') : data.events.length ? t('anLastRun') : t('anStartTitle')),
    running ? h('span.job-pill', h('span.spinner'), t('anWorking')) : null);
  const actions = h('div.row-flex',
    !running ? [scopes.length > 1 ? scopeSel : null, model, h('button.btn.btn-primary', { disabled: !scan || jobRunning(), onclick: () => start(model.value) }, icon('play'), data.events.length ? t('anAgain') : t('anStart'))]
      : h('button.btn', { onclick: stop }, icon('stop'), t('anStop')),
    data.sessionId && !running ? h('button.btn.btn-ghost', { onclick: terminal }, icon('terminal'), t('anTerminal')) : null,
    data.notes ? h('button.btn.btn-ghost', { onclick: () => ctxRef.go(data.scope === 'quarantine' ? 'quarantine' : 'suggestions') }, icon('sparkle'),
      t(data.scope === 'quarantine' ? 'anSeeNotesQ' : 'anSeeNotes', data.notes)) : null);

  progressEl = h('div.an-progress');
  stepsEl = h('div.steps');
  textEl = null; toolCards = new Map();
  live = h('div.card.card-pad.run', { class: 'mt' }, head,
    !running && !data.events.length ? h('p.muted', scan ? t('anStartText', scan.root) : t('anNeedScan')) : null,
    actions, progressEl, stepsEl);
  for (const ev of data.events) addEvent(ev, false);
  updateProgress();
  return live;
}

function updateProgress() {
  if (!progressEl) return;
  const target = data.target || 0;
  const notes = data.events.filter(e => e.kind === 'note').length;
  clear(progressEl);
  if (!target && !notes) return;
  progressEl.append(h('div.row-flex', h('span', t('anProgress', notes, target)), h('span.sp')), meter(target ? Math.min(1, notes / target) : 0, 'accent'));
}

function addEvent(ev, liveUpdate) {
  if (!stepsEl) return;
  switch (ev.kind) {
    case 'init':
      stepsEl.appendChild(step('play', t('anStarted', ev.model || ''), null, 'muted'));
      break;
    case 'tool': {
      if (ev.tool === 'add_note' || ev.tool === 'add_notes') break;
      const card = h('details.tool', h('summary', icon(toolIcon(ev.tool)), h('span', toolLabel(ev.tool)), h('code.small', argsText(ev.input)), h('span.sp'), h('span.spinner')),
        h('pre.tool-io', pretty(ev.input)));
      toolCards.set(ev.toolId, card);
      stepsEl.appendChild(card);
      textEl = null;
      break;
    }
    case 'toolResult': {
      const card = toolCards.get(ev.toolId);
      if (!card) break;
      card.querySelector('.spinner')?.remove();
      if (ev.isError) card.querySelector('summary').appendChild(h('span.chip.danger', t('anToolError')));
      card.appendChild(h('pre.tool-io', pretty(ev.text)));
      break;
    }
    case 'note': {
      const f = findingsById.get(ev.findingId);
      stepsEl.appendChild(h('div.note-card',
        verdictChip(ev.verdict),
        h('div', h('b', f ? f.title : ev.findingId), f ? h('span.muted', ' · ' + size(f.bytes)) : null, h('p', ev.text || ''))));
      textEl = null;
      updateProgress();
      break;
    }
    case 'text':
      if (!textEl) { textEl = h('div.claude-text'); stepsEl.appendChild(h('div.step.text', icon('robot'), textEl)); }
      textEl.textContent += ev.text;
      break;
    case 'result': {
      const ok = ev.subtype === 'success';
      stepsEl.appendChild(step(ok ? 'checkCircle' : 'warn',
        ok ? t('anDone', ev.turns ?? '–', Math.round((ev.durationMs || 0) / 1000)) : ev.subtype === 'error_max_turns' ? t('anMaxTurns') : t('anFailed', ev.subtype || ''),
        null, ok ? 'ok' : 'warn'));
      break;
    }
    case 'error':
      stepsEl.appendChild(step('xCircle', ev.text, null, 'bad'));
      break;
    case 'rateLimit':
      if (liveUpdate && ev.usage) { data.usage = ev.usage; const old = box.querySelector('.grid-hero > :last-child'); old?.replaceWith(usageCard(ev.usage)); }
      break;
    default: break;
  }
  if (liveUpdate) stepsEl.lastElementChild?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
}

const step = (ic, text, extra, cls) => h('div.step' + (cls ? '.' + cls : ''), icon(ic), h('span', text), extra);

export function verdictChip(v) {
  const map = { remove: ['safe', 'vRemove'], check: ['review', 'vCheck'], keep: ['personal', 'vKeep'] };
  const [cls, key] = map[v] || ['neutral', 'vNote'];
  return h('span.chip.' + cls, t(key));
}

const toolIcon = n => ({ get_summary: 'overview', list_findings: 'sparkle', list_quarantine: 'box', inspect_path: 'folder', verify_finding: 'checkCircle', get_diff: 'trend' }[n] || 'wrench');
const toolLabel = n => t('tool_' + n) === 'tool_' + n ? n : t('tool_' + n);

function argsText(input) {
  try {
    const o = JSON.parse(input || '{}');
    const s = Object.entries(o).map(([k, v]) => `${k}=${typeof v === 'string' ? v : JSON.stringify(v)}`).join(' ');
    return s.length > 90 ? s.slice(0, 89) + '…' : s;
  } catch { return ''; }
}

function pretty(s) {
  try { return JSON.stringify(JSON.parse(s), null, 2).slice(0, 4000); } catch { return String(s || '').slice(0, 4000); }
}

async function start(model) {
  const scan = currentScan();
  try {
    await post('/api/analysis/start', { scan: scan.id, model: model || null, scope });
    data = await get('/api/analysis');
    data.running = true;
    draw();
  } catch (e) { toast(e instanceof ApiError && e.status === 409 ? t('jobBusy') : t('errorGeneric', e.message), 'error'); }
}

async function stop() {
  await post('/api/analysis/stop').catch(() => {});
}

async function terminal() {
  const r = await post('/api/analysis/terminal').catch(() => ({ ok: false }));
  if (!r.ok) toast(t('anTerminalFail'), 'warn');
}
