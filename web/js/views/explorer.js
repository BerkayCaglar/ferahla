import { h, clear, size, signed, count, pct, meter, shortDate, toast, skeleton, riskName, tip } from '../ui.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { cached, currentScan } from '../store.js';
import { post } from '../api.js';

let box, ctxRef, tree, sort = { key: 'bytes', dir: -1 }, resizeObs;

export async function render(container, ctx) {
  box = container; ctxRef = ctx;
  const scan = currentScan();
  if (!scan) { ctx.go('overview'); return; }
  box.appendChild(skeleton(8));
  const path = ctx.params.get('path') || '';
  const [tr, findings] = await Promise.all([
    cached('/api/tree?scan=' + scan.id + (path ? '&path=' + encodeURIComponent(path) : '')),
    cached('/api/findings?scan=' + scan.id),
  ]);
  if (!ctx.isCurrent()) return;
  tree = tr;
  draw(findings);
  return () => resizeObs?.disconnect();
}

const open = p => ctxRef.go('explorer', { path: p });

function draw(findings) {
  clear(box);
  box.append(
    h('div.page-head', h('div', h('div.eyebrow', t('exTitle')), h('h1.page-title', tree.path.length <= 3 ? tree.path.slice(0, 2) : tree.path.slice(tree.path.lastIndexOf('\\') + 1), tip(t('exLede'))))),
    crumbs(),
    h('div.explorer',
      h('div.stack', treemap(), table()),
      side(findings)));
}

function crumbs() {
  const parts = [];
  let p = tree.path;
  const root = tree.root;
  while (p && p.length >= root.length) {
    parts.unshift(p);
    if (p.toLowerCase() === root.toLowerCase()) break;
    const i = p.lastIndexOf('\\');
    p = i <= 2 ? p.slice(0, 3) : p.slice(0, i);
  }
  const out = h('nav.crumbs', { 'aria-label': 'Path' });
  parts.forEach((x, i) => {
    if (i) out.appendChild(icon('chevron'));
    const label = i === 0 && x.length <= 3 ? x.slice(0, 2) : x.slice(x.lastIndexOf('\\') + 1);
    out.appendChild(h('button', { text: label, title: x, onclick: () => open(x) }));
  });
  return out;
}

// ---- treemap (squarified) ----

function squarify(values, x, y, w, hgt) {
  const out = [];
  const total = values.reduce((a, v) => a + v.v, 0);
  if (total <= 0 || w <= 0 || hgt <= 0) return out;
  const scale = (w * hgt) / total;
  const items = values.map(v => ({ ...v, a: v.v * scale }));
  let row = [], rect = { x, y, w, h: hgt };
  const worst = (r, side) => {
    const s = r.reduce((a, v) => a + v.a, 0);
    const max = Math.max(...r.map(v => v.a)), min = Math.min(...r.map(v => v.a));
    return Math.max((side * side * max) / (s * s), (s * s) / (side * side * min));
  };
  const layout = r => {
    const s = r.reduce((a, v) => a + v.a, 0);
    if (rect.w >= rect.h) {
      const cw = s / rect.h; let cy = rect.y;
      for (const v of r) { const ch = v.a / cw; out.push({ ...v, x: rect.x, y: cy, w: cw, h: ch }); cy += ch; }
      rect = { x: rect.x + cw, y: rect.y, w: rect.w - cw, h: rect.h };
    } else {
      const ch = s / rect.w; let cx = rect.x;
      for (const v of r) { const cw = v.a / ch; out.push({ ...v, x: cx, y: rect.y, w: cw, h: ch }); cx += cw; }
      rect = { x: rect.x, y: rect.y + ch, w: rect.w, h: rect.h - ch };
    }
  };
  for (const it of items) {
    const side = Math.min(rect.w, rect.h);
    if (row.length === 0 || worst([...row, it], side) <= worst(row, side)) row.push(it);
    else { layout(row); row = [it]; }
  }
  if (row.length) layout(row);
  return out;
}

function treemap() {
  const el = h('div.treemap', { role: 'img', 'aria-label': tree.path });
  const values = tree.children.slice(0, 60).filter(c => c.bytes > 0).map((c, i) => ({ v: c.bytes, c, i }));
  if (tree.otherBytes > tree.bytes * 0.005) values.push({ v: tree.otherBytes, other: true, i: -1 });
  const paint = () => {
    const r = el.getBoundingClientRect();
    if (!r.width) return;
    clear(el);
    const gap = 2;
    for (const cell of squarify(values, 0, 0, r.width, r.height)) {
      const small = cell.w < 70 || cell.h < 34;
      const b = cell.other
        ? h('div.tm-cell.files', { title: `${t('filesHere')} · ${size(cell.v)}` }, h('b', t('filesHere')), h('span', size(cell.v)))
        : h('button.tm-cell.c' + (cell.i % 8), {
          class: (cell.c.suggestedBytes > cell.c.bytes * 0.05 ? 'sugg ' : '') + (small ? 'tiny' : ''),
          title: `${cell.c.name} · ${size(cell.c.bytes)}${cell.c.suggestedBytes ? ` · ${t('suggested')} ${size(cell.c.suggestedBytes)}` : ''}`,
          onclick: () => open(cell.c.path),
        }, h('b', cell.c.name), h('span', size(cell.c.bytes)));
      if (cell.other && small) b.classList.add('tiny');
      b.style.left = cell.x + gap / 2 + 'px'; b.style.top = cell.y + gap / 2 + 'px';
      b.style.width = Math.max(0, cell.w - gap) + 'px'; b.style.height = Math.max(0, cell.h - gap) + 'px';
      el.appendChild(b);
    }
  };
  resizeObs?.disconnect();
  resizeObs = new ResizeObserver(() => paint());
  resizeObs.observe(el);
  return el;
}

// ---- table ----

function table() {
  const rows = [...tree.children];
  const k = sort.key;
  rows.sort((a, b) => (k === 'name' ? a.name.localeCompare(b.name) : (a[k] ?? 0) - (b[k] ?? 0)) * sort.dir * (k === 'name' ? -1 : 1));
  const max = tree.bytes || 1;
  const th = (key, label, cls = '') => h('th' + (cls ? '.' + cls : ''), h('button', {
    onclick: () => { sort = { key, dir: sort.key === key ? -sort.dir : -1 }; clear(box.querySelector('.tbl-wrap')).appendChild(table().firstChild); },
  }, label, sort.key === key ? icon(sort.dir < 0 ? 'chevDown' : 'up') : null));
  const hasGrowth = tree.children.some(c => c.growthBytes !== undefined && c.growthBytes !== null);
  const tbl = h('table.table',
    h('thead', h('tr', th('name', t('name')), th('bytes', t('size'), 'r'), h('th.w-bar.hide-sm', t('share')), th('files', t('filesCol'), 'r.hide-sm'),
      hasGrowth ? th('growthBytes', t('growthCol'), 'r.hide-sm') : null, th('lastWriteUtc', t('modified'), 'hide-sm'))),
    h('tbody',
      tree.parent ? h('tr.click', { onclick: () => open(tree.parent) }, h('td', { colspan: 6 }, h('span.name', icon('up'), h('span', '..')))) : null,
      rows.map(c => h('tr.click', { onclick: () => open(c.path), title: c.path },
        h('td', h('span.name', icon('folder', 'folder'), h('span', c.name),
          c.suggestedBytes > 0 ? h('span.chip.safe', size(c.suggestedBytes)) : null,
          c.markers?.length ? h('span.chip.neutral', c.markers[0]) : null)),
        h('td.r.num', size(c.bytes)),
        h('td.w-bar.hide-sm', meter(c.bytes / max, c.suggestedBytes > c.bytes * 0.5 ? 'safe' : '')),
        h('td.r.num.hide-sm', count(c.files)),
        hasGrowth ? h('td.r.num.hide-sm', c.growthBytes ? h('span', { class: c.growthBytes > 0 ? 'warn' : 'ok' }, signed(c.growthBytes)) : '–') : null,
        h('td.hide-sm.muted', shortDate(c.lastWriteUtc)))),
      tree.otherBytes > 0 ? h('tr', h('td', h('span.name.muted', icon('file'), h('span', t('filesHere')))), h('td.r.num.muted', size(tree.otherBytes)),
        h('td.w-bar.hide-sm', meter(tree.otherBytes / max)), h('td.hide-sm'), hasGrowth ? h('td.hide-sm') : null, h('td.hide-sm')) : null));
  return h('div.card.tbl-wrap', tbl);
}

// ---- side panel ----

function side(findings) {
  const here = new Set(tree.findingIds);
  const list = findings.findings.filter(f => here.has(f.id)).sort((a, b) => b.bytes - a.bytes);
  return h('div.stack',
    h('div.card.card-pad.side-card',
      h('h3', tree.path),
      h('div.big.num', size(tree.bytes)),
      h('dl.kv',
        h('dt', t('filesCol')), h('dd.num', count(tree.files)),
        tree.lastWriteUtc ? [h('dt', t('modified')), h('dd', shortDate(tree.lastWriteUtc))] : null,
        tree.growthBytes ? [h('dt', t('growthCol')), h('dd.num', signed(tree.growthBytes))] : null,
        tree.suggestedBytes ? [h('dt', t('suggested')), h('dd.num.ok', size(tree.suggestedBytes))] : null,
        tree.markers.length ? [h('dt', t('markers')), h('dd', tree.markers.join(', '))] : null),
      h('div.row-flex', { class: 'mt' },
        tree.parent ? h('button.btn.btn-sm', { onclick: () => open(tree.parent) }, icon('up'), t('up')) : null,
        h('button.btn.btn-sm', { onclick: () => post('/api/reveal', { path: tree.path }).catch(e => toast(e.message, 'error')) }, icon('external'), t('showInExplorer')))),
    h('div.card.card-pad.side-card',
      h('div.card-head', icon('sparkle'), h('h2', t('folderFindings'))),
      list.length === 0 ? h('p.muted', t('noFolderFindings'))
        : h('div.f-list', list.slice(0, 8).map(f => h('button.f-link', { onclick: () => ctxRef.go('suggestions', { risk: f.risk }) },
          h('b', f.title), h('span', `${riskName(f.risk)} · ${size(f.bytes)}`))))),
    tree.filesHere.length ? h('div.card.card-pad.side-card',
      h('div.card-head', icon('file'), h('h2', t('notableFiles'))),
      h('ul.paths', tree.filesHere.slice(0, 10).map(f => h('li', h('span', f.name), h('span', size(f.bytes)))))) : null,
    h('p.muted', { class: 'small-note' }, t('foldedNote')));
}
