import { h, clear, size, signed, riskName, catName, date, copyText, toast, dialog, skeleton, setWidth, menu, tip, badge } from '../ui.js';
import { icon, catIcon } from '../icons.js';
import { t } from '../i18n.js';
import { store, cached, currentScan, runJob, jobRunning, dropCache, refreshState } from '../store.js';
import { ApiError, post } from '../api.js';
import { verdictChip } from './analysis.js';
import { progressView, withProgress } from '../progress.js';
import { openChat } from '../chat.js';

const GROUPS = ['safe', 'regenerable', 'review', 'personal'];
let box, ctxRef, data, bar, filter = { q: '', risk: 'all' };

export async function render(container, ctx) {
  box = container; ctxRef = ctx;
  const scan = currentScan();
  if (!scan) { ctx.go('overview'); return; }
  filter.risk = ctx.params.get('risk') || filter.risk || 'all';
  box.appendChild(skeleton(6));
  data = await cached('/api/findings?scan=' + scan.id);
  if (!ctx.isCurrent()) return;
  // Selections of findings that are gone (moved, rescanned) are dropped.
  const ids = new Set(data.findings.map(f => f.id));
  for (const id of [...store.selection]) if (!ids.has(id)) store.selection.delete(id);
  draw();
  return () => bar?.remove();
}

export async function onEvent(what) {
  if (what !== 'state') return;
  const scan = currentScan();
  if (!scan) return;
  data = await cached('/api/findings?scan=' + scan.id);
  const ids = new Set(data.findings.map(f => f.id));
  for (const id of [...store.selection]) if (!ids.has(id)) store.selection.delete(id);
  drawList();
}

let listEl;

function draw() {
  clear(box);
  const search = h('input', { type: 'search', placeholder: t('searchPh'), value: filter.q, 'aria-label': t('search') });
  search.addEventListener('input', () => { filter.q = search.value.trim().toLowerCase(); drawList(); });
  const tabs = h('div.tabs', { role: 'tablist' }, ['all', ...GROUPS].map(r => {
    const n = r === 'all' ? data.findings.length : data.findings.filter(f => f.risk === r).length;
    if (r !== 'all' && n === 0) return null;
    return h('button.tab', { role: 'tab', class: filter.risk === r ? 'on' : '', 'aria-selected': String(filter.risk === r), onclick: () => { filter.risk = r; draw(); } },
      r === 'all' ? t('all') : riskName(r), h('span.count', String(n)));
  }));
  box.append(
    h('div.page-head',
      h('div', h('div.eyebrow', currentScan().root), h('h1.page-title', t('sgTitle'), tip(t('sgLede')))),
      h('div.row-flex',
        h('button.btn', { onclick: () => preset(f => f.risk === 'safe') }, icon('check'), t('presetSafe')),
        data.previousScanId ? h('button.btn', { onclick: () => preset(f => f.isNew || (f.growthBytes || 0) >= 100 * 1048576) }, icon('trend'), t('presetNew')) : null,
        data.findings.some(f => f.noteVerdict === 'remove') ? h('button.btn', { onclick: () => preset(f => f.noteVerdict === 'remove') }, icon('robot'), t('presetClaude')) : null)),
    h('div.toolbar', tabs, h('span.sp'), h('label.input', icon('search'), search)));
  listEl = h('div');
  box.appendChild(listEl);
  drawList();
  ensureBar();
}

const selectable = f => f.action === 'quarantine' && f.items.length > 0 && f.risk !== 'personal' && f.items.every(i => canQuarantine(i.path));

function canQuarantine(path) {
  const root = path.slice(0, 3).toUpperCase();
  const d = store.state?.drives.find(x => x.root.toUpperCase() === root);
  return !d || d.canQuarantine;
}

function matches(f) {
  if (filter.risk !== 'all' && f.risk !== filter.risk) return false;
  if (!filter.q) return true;
  return (f.title + ' ' + f.ruleId + ' ' + (f.reason || '') + ' ' + f.items.map(i => i.path).join(' ')).toLowerCase().includes(filter.q);
}

function preset(fn) {
  for (const f of data.findings) if (selectable(f) && fn(f)) store.selection.add(f.id);
  drawList(); updateBar();
}

function drawList() {
  if (!listEl) return;
  clear(listEl);
  const shown = data.findings.filter(matches);
  if (data.findings.length === 0) { listEl.appendChild(h('div.empty', icon('checkCircle'), h('h3', t('noFindings')))); return; }
  if (shown.length === 0) { listEl.appendChild(h('div.empty', icon('search'), h('h3', t('noMatch')))); return; }
  for (const g of GROUPS) {
    const list = shown.filter(f => f.risk === g);
    if (!list.length) continue;
    const sel = list.filter(selectable);
    const total = list.filter(f => f.action !== 'info').reduce((a, f) => a + f.bytes, 0);
    const all = h('input.check', { type: 'checkbox', 'aria-label': t('selectAll'), disabled: sel.length === 0 });
    const on = sel.filter(f => store.selection.has(f.id)).length;
    all.checked = sel.length > 0 && on === sel.length;
    all.indeterminate = on > 0 && on < sel.length;
    all.addEventListener('change', () => { for (const f of sel) all.checked ? store.selection.add(f.id) : store.selection.delete(f.id); drawList(); updateBar(); });
    listEl.appendChild(h('section.group',
      h('div.group-head', all,
        h('div.g-title', h('h2', riskName(g), tip(t('risk' + g[0].toUpperCase() + g.slice(1) + 'Text')))),
        h('span.chip.' + g, t('nItems', list.length)), h('span.g-sum', size(total))),
      h('div.list', list.map(row))));
  }
  if (data.notSuggested?.length) {
    listEl.appendChild(h('section.not-suggested.card.card-pad',
      h('div.card-head', icon('shield'), h('h2', t('notSuggested')), tip(t('notSuggestedSub'))),
      h('ul.paths', data.notSuggested.slice(0, 12).map(n => h('li', h('span', `${n.path} — ${n.reason}`), h('span', size(n.bytes)))))));
  }
}

function row(f) {
  const can = selectable(f);
  const on = store.selection.has(f.id);
  const cb = h('input.check', { type: 'checkbox', checked: on, disabled: !can, 'aria-label': f.title, title: can ? '' : t('act_' + f.action) || '' });
  const el = h('div.item' + (on ? '.on' : ''));
  cb.addEventListener('change', () => {
    cb.checked ? store.selection.add(f.id) : store.selection.delete(f.id);
    el.classList.toggle('on', cb.checked);
    updateBar(); refreshGroupChecks();
  });
  const top = f.items.filter(i => !i.nestedIn);
  const path = top.length === 0 ? '' : top.length === 1 ? top[0].path : `${top[0].path}  (+${top.length - 1})`;
  const tags = [];
  if (f.isNew && data.previousScanId) tags.push(badge('sparkle', t('isNew'), 'accent'));
  if (f.action !== 'quarantine') tags.push(badge(ACT_ICON[f.action] || 'info', t('act_' + f.action) + ' · ' + t('actHow_' + f.action)));
  const grew = !f.isNew && f.growthBytes && Math.abs(f.growthBytes) >= 100 * 1048576 ? t('grew', signed(f.growthBytes)) : '';

  const details = h('details',
    h('summary', icon('chevron'), t('details')),
    f.evidence?.length ? h('div.i-ev', f.evidence.map(e => h('span.ev', icon('check'), e))) : null,
    h('dl.kv',
      f.whatBreaks ? [h('dt', t('whatBreaks')), h('dd', f.whatBreaks)] : null,
      f.howToRestore ? [h('dt', t('howRestore')), h('dd', f.howToRestore)] : null,
      f.lastWriteUtc ? [h('dt', t('lastChange')), h('dd', date(f.lastWriteUtc, false))] : null,
      [h('dt', t('rule')), h('dd.mono', f.ruleId)]),
    f.action !== 'quarantine' ? h('p', { class: 'how' }, t('actHow_' + f.action)) : null,
    f.command ? h('div.cmdline', h('code', f.command), h('button.btn.btn-sm', { text: t('copy'), onclick: e => copyText(f.command, e.currentTarget) })) : null,
    h('div.row-flex.actions-row',
      f.items.length && f.action !== 'info' ? h('button.btn.btn-sm', { onclick: e => verify(f, e.currentTarget) }, icon('checkCircle'), t('verify')) : null,
      (SYSTEM[f.ruleId] || []).map(([id, label]) => h('button.btn.btn-sm', { onclick: () => runSystem(id) }, icon('shield'), t(label))),
      f.action === 'uninstall' || f.action === 'launcher' ? h('button.btn.btn-sm', { onclick: () => ctxRef.go('apps', { tab: f.category === 'game' ? 'games' : 'apps' }) }, icon('apps'), t('openApps')) : null),
    h('div.verify-out'),
    top.length > 1 ? h('ul.paths', top.slice(0, 30).map(i => h('li', h('span', i.path), h('span', size(i.bytes))))) : null);

  el.append(cb,
    h('div.i-main',
      h('div.i-title', catIcon(f.category), h('span', f.title), tags),
      path ? h('div.i-path', path) : null,
      f.reason ? clampable(h('div.i-reason.clamp', f.reason)) : null,
      f.note ? h('div.ai-note', h('div.ai-head', tip(f.noteSource === 'claude' ? t('claudeSays') : t('yourNote'), f.noteSource === 'claude' ? 'robot' : 'note'), verdictChip(f.noteVerdict)), clampable(h('p.clamp', f.note))) : null,
      h('div.i-more', details)),
    h('div.i-size', size(f.bytes), grew ? h('small', grew) : f.action === 'info' ? h('small', t('act_info')) : null,
      h('div.row-icons',
        h('button.icon-btn.ask', { title: t('chAskClaude'), 'aria-label': t('chAskClaude'), onclick: () => openChat(f, ctxRef.go) }, icon('robot')),
        h('button.icon-btn', { title: f.note ? t('editNote') : t('addNote'), 'aria-label': f.note ? t('editNote') : t('addNote'), onclick: () => editNote(f) }, icon('note')),
        top.length ? revealButton(top) : null)));
  return el;
}

const ACT_ICON = { command: 'terminal', uninstall: 'apps', launcher: 'game', info: 'info' };

/** Long text shows two lines; a click opens the rest (and closes it again). */
function clampable(el) {
  el.addEventListener('click', () => el.classList.toggle('open'));
  requestAnimationFrame(() => { if (el.scrollHeight <= el.clientHeight + 2) el.classList.add('fits'); });
  return el;
}

/** Opens Explorer on the item; with several top-level items, a menu picks which one. */
function revealButton(top) {
  const open = p => post('/api/reveal', { path: p }).catch(e => toast(e.message, 'error'));
  const btn = h('button.icon-btn', {
    title: t('showInExplorer'), 'aria-label': t('showInExplorer'),
    onclick: () => top.length === 1 ? open(top[0].path)
      : menu(btn, top.slice(0, 20).map(i => ({ icon: 'folder', title: i.path.slice(i.path.lastIndexOf('\\') + 1) || i.path, sub: i.path, right: size(i.bytes), run: () => open(i.path) }))),
  }, icon('external'));
  return btn;
}

function refreshGroupChecks() {
  for (const sec of listEl.querySelectorAll('section.group')) {
    const boxes = [...sec.querySelectorAll('.list .check:not(:disabled)')];
    const all = sec.querySelector('.group-head .check');
    const on = boxes.filter(b => b.checked).length;
    all.checked = boxes.length > 0 && on === boxes.length;
    all.indeterminate = on > 0 && on < boxes.length;
  }
}

// ---- deep checks and system actions ----

const SYSTEM = {
  'system.hiberfil': [['hibernate-off', 'sysHibernate']],
  'system.component-store': [['component-analyze', 'sysAnalyze'], ['component-cleanup', 'sysCleanup']],
  'system.unaccounted': [['shadow-storage', 'sysShadow']],
};

async function verify(f, btn) {
  const out = btn.closest('details').querySelector('.verify-out');
  btn.disabled = true;
  clear(out).appendChild(h('div.step', h('span.spinner'), t('verifying')));
  try {
    const r = await post('/api/verify', { findingId: f.id, scan: data.scanId });
    clear(out).appendChild(h('div.verify-box' + (r.ok ? '.ok' : '.warn'),
      h('b', icon(r.ok ? 'checkCircle' : 'warn'), r.ok ? t('verified') : t('notVerified')),
      h('ul', r.checks.map(c => h('li', c)))));
  } catch (e) { clear(out).appendChild(h('div.note.danger', icon('xCircle'), h('div', t('errorGeneric', e.message)))); }
  btn.disabled = false;
}

async function editNote(f) {
  let verdict = f.noteVerdict && f.noteVerdict !== 'none' ? f.noteVerdict : 'check';
  let area;
  const text = await dialog((close, re) => {
    area ??= h('textarea.text-input', { rows: 4, placeholder: t('notePh'), 'aria-label': t('notePh') });
    if (!area.value && f.note) area.value = f.note;
    const pick = v => h('button', { class: verdict === v ? 'on' : '', type: 'button', onclick: () => { verdict = v; re(); } }, t({ remove: 'vRemove', check: 'vCheck', keep: 'vKeep' }[v]));
    return {
      icon: 'note', title: f.note ? t('editNote') : t('addNote'), text: f.title,
      body: h('div.stack.tight', area, h('div.seg', ['remove', 'check', 'keep'].map(pick))),
      actions: [{ label: t('cancel'), kind: 'ghost', value: undefined }, { label: t('save'), kind: 'primary', run: c => c(area.value) }],
    };
  });
  if (text === undefined) return;
  try { await post('/api/note', { findingId: f.id, text, verdict }); }
  catch (e) { toast(t('errorGeneric', e.message), 'error'); }
}

async function runSystem(id) {
  const ok = await dialog(() => ({
    icon: 'shield', title: t('sysTitle'), text: t('sysText'),
    actions: [{ label: t('cancel'), kind: 'ghost', value: false }, { label: t('sysRun'), kind: 'primary', value: true }],
  }));
  if (!ok) return;
  const r = await post('/api/system', { id }).catch(e => ({ ok: false, message: e.message }));
  toast(r.ok ? t('sysStarted', r.message || '') : t('sysDeclined'), r.ok ? 'ok' : 'warn');
}

// ---- action bar ----

function selected() { return data.findings.filter(f => store.selection.has(f.id)); }

function ensureBar() {
  bar?.remove();
  bar = h('div.actionbar', { role: 'region', 'aria-label': t('moveToQuarantine') });
  document.body.appendChild(bar);
  updateBar();
}

function updateBar() {
  if (!bar) return;
  const s = selected();
  const bytes = s.reduce((a, f) => a + f.bytes, 0);
  const total = data.totalBytes || 1, free = data.freeBytes;
  const used = h('i'), gain = h('i'), rest = h('i');
  used.style.background = 'var(--used)'; gain.style.background = 'var(--gain)'; rest.style.background = 'var(--free)';
  used.style.flex = String(Math.max(0, total - free - bytes)); gain.style.flex = String(bytes); rest.style.flex = String(free);
  clear(bar).append(
    h('div', h('div.a-num', size(bytes)), h('div.a-sub', t('selected', s.length))),
    h('div.a-gauge', h('div.stackbar', used, gain, rest), h('div.a-sub', t('freeAfter', size(free), size(free + bytes)))),
    h('span.sp'),
    h('button.btn.btn-ghost', { onclick: () => { store.selection.clear(); drawList(); updateBar(); } }, t('presetClear')),
    h('button.btn.btn-primary.btn-lg', { disabled: s.length === 0, onclick: () => quarantineFlow(s) }, icon('box'), t('moveToQuarantine')));
  bar.classList.toggle('show', s.length > 0);
}

// ---- quarantine flow: dry run → confirm → move → result (with undo) ----

function planFor(list) {
  return {
    schema: 1, kind: 'ferahla-plan', scanId: data.scanId, createdAt: new Date().toISOString(),
    findings: list.map(f => ({ id: f.id, ruleId: f.ruleId, action: f.action, items: f.items.filter(i => !i.nestedIn).map(i => ({ path: i.path, bytes: i.bytes })) })),
  };
}

async function quarantineFlow(list) {
  if (jobRunning()) { toast(t('jobBusy'), 'warn'); return; }
  const plan = planFor(list);
  let preview = null, error = null;
  const run = runJob('/api/quarantine', { plan, dryRun: true }).then(j => { preview = j; }, e => { error = e; });
  const checking = progressView();
  let rerender;
  const result = dialog((close, re) => {
    rerender = re;
    if (error) return { icon: 'xCircle', tone: 'danger', title: t('pvTitle'), text: t('errorGeneric', error.message), actions: [{ label: t('close'), kind: 'primary' }] };
    if (!preview) return { icon: 'box', title: t('pvTitle'), text: t('pvChecking'), body: checking.el };
    const r = preview.result || { items: [], done: 0, doneBytes: 0, skipped: 0, failed: 0 };
    if (preview.state !== 'done') return { icon: 'xCircle', tone: 'danger', title: t('pvTitle'), text: t('errorGeneric', preview.error || preview.state), actions: [{ label: t('close') }] };
    const skipped = r.items.filter(o => o.status !== 'wouldMove');
    return {
      icon: 'box', title: t('pvTitle'), text: t('pvText'),
      body: h('div',
        h('div.sum-strip',
          h('div', h('b', size(r.doneBytes)), h('span', t('pvWill', r.done, size(r.doneBytes)))),
          skipped.length ? h('div', h('b.warn', String(skipped.length)), h('span', t('pvSkip', skipped.length))) : null),
        outcomes(r.items, true)),
      actions: [
        { label: t('cancel'), kind: 'ghost', value: false },
        { label: t('pvConfirm'), kind: 'primary', disabled: r.done === 0, value: true },
      ],
    };
  });
  await run;
  checking.stop();
  rerender?.();
  if (!await result) return;

  let job;
  try { job = await withProgress({ icon: 'box', title: t('progMoving'), text: t('progKeepOpen') }, () => runJob('/api/quarantine', { plan, dryRun: false })); }
  catch (e) { toast(e instanceof ApiError && e.status === 409 ? t('jobBusy') : t('errorGeneric', e.message), 'error'); return; }
  const r = job.result || { items: [], done: 0, doneBytes: 0 };
  for (const f of list) store.selection.delete(f.id);
  dropCache(); refreshState().catch(() => {});
  showResult(r);
}

function showResult(r) {
  const problems = r.items.filter(o => o.status === 'failed' || o.status === 'skipped');
  const title = r.done === 0 ? t('resTitleNone') : problems.length ? t('resTitlePartial') : t('resTitle');
  dialog(close => ({
    icon: r.done === 0 ? 'warn' : 'checkCircle', tone: r.done === 0 ? 'danger' : undefined, title,
    text: r.done ? t('resFreed', size(r.doneBytes)) : '',
    body: h('div',
      h('div.sum-strip',
        h('div', h('b.ok', String(r.done)), h('span', t('resMoved'))),
        h('div', h('b.warn', String(r.skipped || 0)), h('span', t('resSkipped'))),
        h('div', h('b.bad', String(r.failed || 0)), h('span', t('resFailed')))),
      outcomes(problems.length ? problems : r.items, false)),
    left: r.batch ? h('button.btn.btn-ghost', {
      onclick: async () => {
        close();
        const j = await withProgress({ icon: 'undo', title: t('progRestoring'), text: t('progKeepOpen') }, () => runJob('/api/restore', { batch: r.batch }));
        dropCache(); refreshState().catch(() => {});
        toast(j.result?.done ? t('undone') : t('errorGeneric', j.error || ''), j.result?.done ? 'ok' : 'error');
      },
    }, icon('undo'), t('undo')) : null,
    actions: [
      r.batch ? { label: t('goQuarantine'), kind: 'ghost', run: c => { c(); ctxRef.go('quarantine'); } } : null,
      { label: t('close'), kind: 'primary' },
    ].filter(Boolean),
  }));
}

export function outcomes(items, preview) {
  const icoFor = s => s === 'moved' || s === 'wouldMove' || s === 'restored' || s === 'wouldRestore' || s === 'deleted' ? ['checkCircle', 'ok']
    : s === 'skipped' ? ['skip', 'warn'] : ['xCircle', 'bad'];
  return h('div.outcomes', items.slice(0, 200).map(o => {
    const [ic, cls] = icoFor(o.status);
    return h('div.outcome', icon(ic, cls), h('div', h('div.o-path', o.path), o.reason ? h('div.o-why', o.reason) : null), h('span.o-size', size(o.bytes)));
  }));
}
