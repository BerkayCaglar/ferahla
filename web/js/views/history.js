import { h, clear, size, signed, count, date, shortDate, confirm, toast, meter, skeleton } from '../ui.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { store, scansOf, cached, dropCache, refreshState } from '../store.js';
import { post } from '../api.js';

let box, ctxRef, fromId = null;

export async function render(container, ctx) {
  box = container; ctxRef = ctx;
  await draw();
}

export function onEvent(what) { if (what === 'state') draw(); }

async function draw() {
  const scans = scansOf(store.root);
  if (!scans.length) { ctxRef.go('overview'); return; }
  if (fromId && !scans.some(s => s.id === fromId)) fromId = null;
  const diff = scans.length > 1 ? await cached('/api/diff?scan=' + scans[0].id + (fromId ? '&from=' + encodeURIComponent(fromId) : '')) : null;
  if (!ctxRef.isCurrent()) return;
  clear(box).append(
    h('div.page-head', h('div', h('div.eyebrow', store.root), h('h1.page-title', t('hiTitle')), h('p.page-lede', t('hiLede')))),
    h('div.card.card-pad', h('div.card-head', icon('trend'), h('h2', t('hiFree'))),
      scans.length > 1 ? chart([...scans].reverse()) : h('p.muted', t('hiOnlyOne'))),
    h('div.grid.grid-2', { class: 'mt' },
      h('div.card.card-pad', h('div.card-head', icon('history'), h('h2', t('hiScans'))), list(scans)),
      h('div.card.card-pad', h('div.card-head', icon('layers'), h('h2', t('hiCompare')), scans.length > 2 ? picker(scans) : null), changes(diff))));
  box.querySelector('.mt').style.marginTop = '18px';
}

function chart(scans) {
  const ns = 'http://www.w3.org/2000/svg';
  const W = 720, H = 220, pad = { l: 56, r: 16, t: 14, b: 28 };
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
  svg.setAttribute('class', 'chart');
  svg.setAttribute('preserveAspectRatio', 'none');
  const add = (tag, attrs, text) => { const e = document.createElementNS(ns, tag); for (const k in attrs) e.setAttribute(k, attrs[k]); if (text) e.textContent = text; svg.appendChild(e); return e; };
  const xs = scans.map(s => new Date(s.scannedAt).getTime());
  const ys = scans.map(s => s.freeBytes);
  const x0 = Math.min(...xs), x1 = Math.max(...xs) || x0 + 1;
  const ymax = Math.max(...ys) * 1.12 || 1, ymin = 0;
  const X = x => pad.l + (x1 === x0 ? (W - pad.l - pad.r) / 2 : (x - x0) / (x1 - x0) * (W - pad.l - pad.r));
  const Y = y => pad.t + (1 - (y - ymin) / (ymax - ymin)) * (H - pad.t - pad.b);
  for (let i = 0; i <= 4; i++) {
    const v = ymin + (ymax - ymin) * i / 4;
    add('line', { x1: pad.l, x2: W - pad.r, y1: Y(v), y2: Y(v), class: 'grid-line' });
    add('text', { x: pad.l - 8, y: Y(v) + 4, 'text-anchor': 'end', class: 'axis' }, size(v));
  }
  const pts = scans.map((s, i) => [X(xs[i]), Y(ys[i])]);
  add('path', { d: `M${pts[0][0]},${Y(0)} ` + pts.map(p => `L${p[0]},${p[1]}`).join(' ') + ` L${pts[pts.length - 1][0]},${Y(0)} Z`, class: 'area' });
  add('path', { d: pts.map((p, i) => (i ? 'L' : 'M') + p[0] + ',' + p[1]).join(' '), class: 'line' });
  pts.forEach((p, i) => {
    const c = add('circle', { cx: p[0], cy: p[1], r: 4, class: 'pt' });
    const tip = document.createElementNS(ns, 'title'); tip.textContent = `${date(scans[i].scannedAt)} · ${size(scans[i].freeBytes)}`; c.appendChild(tip);
  });
  const first = scans[0], last = scans[scans.length - 1];
  add('text', { x: pad.l, y: H - 8, class: 'axis' }, shortDate(first.scannedAt));
  add('text', { x: W - pad.r, y: H - 8, 'text-anchor': 'end', class: 'axis' }, shortDate(last.scannedAt));
  return svg;
}

function list(scans) {
  const max = Math.max(...scans.map(s => s.totalBytes || 0)) || 1;
  return h('div.list', scans.map((s, i) => h('div.item',
    icon('scan'),
    h('div.i-main',
      h('div.i-title', h('span', date(s.scannedAt)), i === 0 ? h('span.chip.accent', t('latest')) : null),
      h('div.i-reason', `${count(s.files)} ${t('statFiles')} · ${t('freeOf', size(s.freeBytes), size(s.totalBytes))}`),
      meter(s.totalBytes ? (s.totalBytes - s.freeBytes) / max : 0)),
    h('div.i-size', i < scans.length - 1 ? h('small', signed(s.freeBytes - scans[i + 1].freeBytes)) : null,
      h('button.btn.btn-sm.btn-ghost', { onclick: () => remove(s) }, icon('trash'), t('hiDelete'))))));
}

function picker(scans) {
  const sel = h('select.select', { 'aria-label': t('hiCompareWith') },
    scans.slice(1).map((s, i) => h('option', { value: s.id, text: t('hiSince', date(s.scannedAt, false)), selected: fromId ? s.id === fromId : i === 0 })));
  sel.addEventListener('change', () => { fromId = sel.value; draw(); });
  return sel;
}

function changes(diff) {
  if (!diff || !diff.from) return h('p.muted', t('hiOnlyOne'));
  const entries = diff.entries.slice(0, 12);
  if (!entries.length) return h('p.muted', t('growthFree', signed(diff.freeBytesDelta)));
  const max = Math.max(...entries.map(e => Math.abs(e.delta))) || 1;
  return h('div.bars', entries.map(e => h('button.bar-row', { onclick: () => ctxRef.go('explorer', { path: e.path }), title: e.path },
    h('span.b-lab', e.path), meter(Math.abs(e.delta) / max, e.delta > 0 ? 'review' : 'safe'), h('span.b-val', signed(e.delta)))));
}

async function remove(s) {
  if (!await confirm(t('hiDelete'), t('hiDeleteConfirm'), t('hiDelete'), 'danger')) return;
  try { await post('/api/scans/delete', { id: s.id }); dropCache(); await refreshState(); draw(); }
  catch (e) { toast(t('errorGeneric', e.message), 'error'); }
}
