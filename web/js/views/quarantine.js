import { h, clear, size, count, date, ago, toast, dialog, skeleton, tip } from '../ui.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { store, runJob, jobRunning, dropCache, refreshState } from '../store.js';
import { get, ApiError } from '../api.js';
import { outcomes } from './suggestions.js';
import { withProgress } from '../progress.js';
import { verdictChip } from './analysis.js';

let box, ctxRef, batches = [], notes = {}, picked = new Set(); // keys "batch|key"; notes by finding id

export async function render(container, ctx) {
  box = container; ctxRef = ctx;
  box.appendChild(skeleton(5));
  await load();
}

export function onEvent(what) { if (what === 'state') load(); }

async function load() {
  [batches, notes] = await Promise.all([get('/api/quarantine'), get('/api/notes').then(n => n.notes || {}, () => ({}))]);
  if (!ctxRef.isCurrent()) return;
  const live = new Set(batches.flatMap(b => b.entries.map(e => b.batch + '|' + e.key)));
  for (const k of [...picked]) if (!live.has(k)) picked.delete(k);
  draw();
}

function draw() {
  clear(box);
  const items = batches.reduce((a, b) => a + b.items, 0);
  const bytes = batches.reduce((a, b) => a + b.bytes, 0);
  box.append(
    h('div.page-head', h('div', h('div.eyebrow', t('navQuarantine')), h('h1.page-title', t('qTitle'), tip(t('qLede')))),
      h('div.row-flex',
        batches.length ? h('button.btn', { dataset: { tip: t('qAskClaudeText') }, class: 'tip-end', onclick: () => ctxRef.go('analysis', { scope: 'quarantine' }) }, icon('robot'), t('qAskClaude')) : null,
        picked.size ? [
          h('button.btn', { onclick: () => act('restore', selection()) }, icon('undo'), t('restoreSel')),
          h('button.btn.btn-danger', { onclick: () => act('delete', selection()) }, icon('trash'), t('deleteSel'))] : null)));
  if (!batches.length) {
    box.appendChild(h('div.card', h('div.empty', icon('box'), h('h3', t('qEmpty')), h('p', t('qEmptyText')))));
    box.appendChild(h('div.note', { class: 'mt' }, icon('info'), h('div', t('qWhere'))));
    return;
  }
  box.append(
    h('div.qstats',
      tile(size(bytes), t('qHeld')), tile(count(items), t('qItems')), tile(count(batches.length), t('qBatches'))),
    ...batches.map(batchCard),
    h('div.note', { class: 'mt' }, icon('info'), h('div', t('qWhere'))));
  for (const n of box.querySelectorAll('.mt')) n.style.marginTop = '18px';
}

const tile = (v, label) => h('div.tile', h('b.num', v), h('span.t-sub', label));

function selection() {
  const by = new Map();
  for (const k of picked) {
    const [b, key] = k.split('|');
    if (!by.has(b)) by.set(b, []);
    by.get(b).push(key);
  }
  return by;
}

function batchCard(b) {
  const keys = b.entries.map(e => b.batch + '|' + e.key);
  const all = h('input.check', { type: 'checkbox', 'aria-label': t('selectAll') });
  const on = keys.filter(k => picked.has(k)).length;
  all.checked = on > 0 && on === keys.length;
  all.indeterminate = on > 0 && on < keys.length;
  all.addEventListener('change', () => { for (const k of keys) all.checked ? picked.add(k) : picked.delete(k); draw(); });
  return h('div.card.batch',
    h('div.batch-head', all,
      h('div', h('h3', t('qBatch', ago(b.createdAt))), h('div.muted', `${date(b.createdAt)} · ${b.volumes.join(' ')} · ${t('nItems', b.items)}`)),
      h('span.sp'), h('b.num', size(b.bytes)),
      h('button.btn.btn-sm', { onclick: () => act('restore', new Map([[b.batch, null]])) }, icon('undo'), t('restore')),
      h('button.btn.btn-sm.btn-ghost', { onclick: () => act('delete', new Map([[b.batch, null]])) }, icon('trash'), t('deleteForGood'))),
    h('div.list', b.entries.map(e => {
      const k = b.batch + '|' + e.key;
      const cb = h('input.check', { type: 'checkbox', checked: picked.has(k), 'aria-label': e.originalPath });
      cb.addEventListener('change', () => { cb.checked ? picked.add(k) : picked.delete(k); draw(); });
      return h('div.item' + (picked.has(k) ? '.on' : ''), cb,
        h('div.i-main', h('div.i-title', icon(e.isDir ? 'folder' : 'file'), h('span', e.originalPath.slice(e.originalPath.lastIndexOf('\\') + 1))),
          h('div.i-path', e.originalPath),
          e.ruleId ? h('div.i-ev', h('span.ev', e.ruleId), e.files > 1 ? h('span.ev', `${count(e.files)} ${t('statFiles')}`) : null) : null,
          noteOf(e)),
        h('div.i-size', size(e.bytes)));
    })));
}

function noteOf(e) {
  const n = e.findingId && notes[e.findingId];
  if (!n) return null;
  return h('div.ai-note', h('div.ai-head', icon(n.source === 'claude' ? 'robot' : 'note'), h('b', n.source === 'claude' ? t('claudeSays') : t('yourNote')), verdictChip(n.verdict)), h('p', n.text));
}

/** sel: Map(batch -> keys[] | null for the whole batch) */
async function act(kind, sel) {
  if (jobRunning()) { toast(t('jobBusy'), 'warn'); return; }
  const entries = [...sel].flatMap(([b, keys]) => {
    const batch = batches.find(x => x.batch === b);
    return batch ? batch.entries.filter(e => !keys || keys.includes(e.key)) : [];
  });
  const bytes = entries.reduce((a, e) => a + e.bytes, 0);
  if (kind === 'delete') {
    let ack = false;
    const ok = await dialog((close, re) => ({
      icon: 'trash', tone: 'danger', title: t('delTitle'), text: t('delText', entries.length, size(bytes)),
      body: h('div',
        h('ul.paths', entries.slice(0, 12).map(e => h('li', h('span', e.originalPath), h('span', size(e.bytes))))),
        h('label.confirm-box', h('input.check', { type: 'checkbox', checked: ack, onchange: e => { ack = e.target.checked; re(); } }), h('span', t('delAck')))),
      actions: [{ label: t('cancel'), kind: 'ghost', value: false }, { label: t('deleteForGood'), kind: 'danger', disabled: !ack, value: true }],
    }));
    if (!ok) return;
  }
  const results = [];
  const prog = kind === 'delete' ? { icon: 'trash', title: t('progDeleting') } : { icon: 'undo', title: t('progRestoring') };
  await withProgress({ ...prog, text: t('progKeepOpen') }, async () => {
    for (const [batch, keys] of sel) {
      try {
        const j = await runJob(kind === 'delete' ? '/api/delete' : '/api/restore', { batch, keys: keys || undefined, confirm: kind === 'delete' ? 'delete' : undefined });
        if (j.result) results.push(...j.result.items);
        else if (j.error) toast(t('errorGeneric', j.error), 'error');
      } catch (e) { toast(e instanceof ApiError && e.status === 409 ? t('jobBusy') : t('errorGeneric', e.message), 'error'); }
    }
  });
  picked.clear();
  dropCache(); await refreshState().catch(() => {});
  await load();
  const done = results.filter(o => o.status === 'restored' || o.status === 'deleted');
  const doneBytes = done.reduce((a, o) => a + o.bytes, 0);
  const problems = results.filter(o => o.status === 'failed' || o.status === 'skipped');
  if (problems.length) {
    dialog(() => ({ icon: 'warn', title: kind === 'delete' ? t('delDone', size(doneBytes)) : t('restDone', done.length), body: outcomes(problems), actions: [{ label: t('close'), kind: 'primary' }] }));
  } else if (done.length) {
    toast(kind === 'delete' ? t('delDone', size(doneBytes)) : t('restDone', done.length), 'ok');
  }
}
