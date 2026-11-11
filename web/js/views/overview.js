import { h, clear, size, signed, count, meter, riskName, catName, setWidth, tip } from '../ui.js';
import { icon, brandMark, catIcon } from '../icons.js';
import { t } from '../i18n.js';
import { store, cached, currentScan, selectRoot } from '../store.js';
import { post } from '../api.js';

let box, ctxRef, elapsedTimer;

export async function render(container, ctx) {
  box = container; ctxRef = ctx;
  return draw();
}

export function onEvent(what) {
  if (what === 'job') {
    const j = store.state?.job;
    if (j?.kind === 'scan' && j.state === 'running') {
      if (!box.querySelector('.scanning')) draw(); else updateScan(j);
    } else if (box.querySelector('.scanning')) draw(); // cancelled or failed: back to the normal page
  }
  if (what === 'state' && !box.querySelector('.scanning')) {
    // e.g. items went to quarantine from another tab: numbers change
    draw();
  }
}

async function draw() {
  clearInterval(elapsedTimer);
  const job = store.state?.job;
  const scan = currentScan();
  if (job?.kind === 'scan' && job.state === 'running') { clear(box).appendChild(scanning(job)); return () => clearInterval(elapsedTimer); }
  if (!scan) { clear(box).appendChild(welcome()); return; }
  if (!box.firstChild) box.appendChild(h('div', h('div.skeleton.sk-line'), h('div.skeleton.sk-block')));

  const [findings, tree, diff] = await Promise.all([
    cached('/api/findings?scan=' + scan.id),
    cached('/api/tree?scan=' + scan.id),
    cached('/api/diff?scan=' + scan.id),
  ]);
  if (!ctxRef.isCurrent()) return;
  clear(box);
  const actionable = findings.findings.filter(f => f.action !== 'info' && f.risk !== 'personal');
  const suggest = actionable.reduce((a, f) => a + f.bytes, 0);

  box.append(
    h('div.page-head', h('div',
      h('div.eyebrow', `${scan.root} · ${count(scan.files)} ${t('statFiles')}`),
      h('h1.page-title', t(scan.root.length <= 3 ? 'ovTitle' : 'ovTitleFolder', driveName(scan.root))))),
    h('div.grid.grid-hero',
      hero(findings, suggest, actionable.length),
      kinds(actionable)),
    composition(findings, scan),
    h('div.grid.grid-2', { class: 'mt' }, whereCard(tree), growthCard(diff)),
    winsCard(actionable) || '');
  const n = notes(findings, scan);
  if (n) box.appendChild(n);
  box.querySelector('.mt').style.marginTop = '18px';
}

const driveName = root => root.length <= 3 ? root.slice(0, 2) : root.slice(root.lastIndexOf('\\') + 1);

function hero(f, suggest, n) {
  const total = f.totalBytes || 1, free = f.freeBytes, used = total - free;
  const gain = Math.min(suggest, used);
  const R = 70, C = 2 * Math.PI * R;
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 0 168 168');
  const circ = (cls, len, offset = 0) => {
    const c = document.createElementNS(ns, 'circle');
    c.setAttribute('cx', 84); c.setAttribute('cy', 84); c.setAttribute('r', R); c.setAttribute('class', cls);
    c.setAttribute('stroke-dasharray', `0 ${C}`);
    c.setAttribute('stroke-dashoffset', -offset);
    requestAnimationFrame(() => requestAnimationFrame(() => c.setAttribute('stroke-dasharray', `${len} ${C - len}`)));
    svg.appendChild(c);
  };
  circ('track', C);
  circ('used', C * (used - gain) / total);
  if (gain > 0) circ('gain', C * gain / total, C * (used - gain) / total);
  const pre = t('ovHeadline', '\u0001').split('\u0001');

  return h('div.card.hero',
    h('div.donut', svg, h('div.donut-center', h('b.num', size(free)), h('span', t('ovFree')))),
    h('div.hero-text',
      suggest > 0 ? h('h2', pre[0], h('em', size(suggest)), pre[1] || '') : h('h2', t('ovHeadlineNone')),
      h('p', suggest > 0 ? t('ovLede', n) : t('ovLedeNone')),
      h('div.legend',
        h('span', h('i.dot.used'), `${t('used')} ${size(used)}`),
        gain > 0 ? h('span', h('i.dot.gain'), `${t('canFree')} ${size(gain)}`) : null,
        h('span', h('i.dot.free'), `${t('free')} ${size(free)}`)),
      h('div.row-flex',
        suggest > 0 ? h('button.btn.btn-primary', { onclick: () => ctxRef.go('suggestions') }, icon('sparkle'), t('review')) : null,
        h('button.btn', { onclick: () => ctxRef.go('explorer') }, icon('folder'), t('openExplorer')))));
}

/**
 * Where all of the used space goes, not only the suggested part: what Ferahla suggests (by risk and by how it is
 * removed), and the rest, which is the user's own files, apps and Windows. Each part opens where it is handled.
 */
function composition(f, scan) {
  const drive = scan.root.length <= 3;
  // For a folder scan there is no drive free space to show; the base is what the scan found.
  const base = drive ? Math.max(0, (f.totalBytes || 0) - (f.freeBytes || 0)) : scan.scannedBytes || 0;
  const sum = pred => f.findings.filter(pred).reduce((a, x) => a + x.bytes, 0);
  const moves = x => x.action === 'quarantine';
  const parts = [
    { k: 'safe', ic: 'check', label: riskName('safe'), bytes: sum(x => moves(x) && x.risk === 'safe'), go: ['suggestions', { risk: 'safe' }] },
    { k: 'regen', ic: 'undo', label: riskName('regenerable'), bytes: sum(x => moves(x) && x.risk === 'regenerable'), go: ['suggestions', { risk: 'regenerable' }] },
    { k: 'review', ic: 'search', label: riskName('review'), bytes: sum(x => moves(x) && (x.risk === 'review' || x.risk === 'personal')), go: ['suggestions', { risk: 'review' }] },
    { k: 'apps', ic: 'apps', label: t('coApps'), bytes: sum(x => x.action === 'uninstall' || x.action === 'launcher'), go: ['apps'] },
    { k: 'system', ic: 'terminal', label: t('coSystem'), bytes: sum(x => x.action === 'command'), go: ['suggestions'] },
    { k: 'info', ic: 'info', label: t('coInfo'), bytes: sum(x => x.action === 'info' && x.ruleId !== 'system.unaccounted'), go: ['suggestions'] },
  ].filter(p => p.bytes > 0);
  // Findings can overlap a little (a folder in two rules); never claim more than is used.
  const found = parts.reduce((a, p) => a + p.bytes, 0);
  if (found > base && found > 0) for (const p of parts) p.bytes = p.bytes * base / found;
  parts.push({ k: 'rest', ic: 'lock', label: t('coRest'), tip: t('coRestTip'), bytes: Math.max(0, base - Math.min(found, base)), go: ['explorer'] });
  if (drive) parts.push({ k: 'free', ic: 'drive', label: t('free'), bytes: f.freeBytes || 0 });
  const all = parts.reduce((a, p) => a + p.bytes, 0) || 1;

  const bar = h('div.stackbar', { role: 'img', 'aria-label': t('coTitle') });
  let at = 0;
  for (const p of parts) {
    // Tooltips of the right half open to the left, so they stay on the page.
    const seg = h('button.k-' + p.k + ((at += p.bytes / all) - p.bytes / all / 2 > 0.5 ? '.tip-end' : ''), { dataset: { tip: `${p.label} · ${size(p.bytes)}` }, 'aria-label': `${p.label} ${size(p.bytes)}`, onclick: () => p.go && ctxRef.go(...p.go) });
    seg.style.flex = String(p.bytes / all);
    bar.appendChild(seg);
  }
  return h('div.card.card-pad.compo',
    h('div.card-head', icon('layers'), h('h2', t('coTitle')), tip(t('coTip'))),
    bar,
    h('div.compo-legend', parts.map(p => h(p.go ? 'button.compo-item' : 'div.compo-item', { onclick: p.go ? () => ctxRef.go(...p.go) : undefined, dataset: p.tip ? { tip: p.tip } : undefined },
      h('span.ci-ico.k-' + p.k, icon(p.ic)), h('span.ci-name', p.label), h('span.ci-val', size(p.bytes))))));
}

function kinds(list) {
  const by = new Map();
  for (const f of list) by.set(f.category, (by.get(f.category) || 0) + f.bytes);
  const rows = [...by.entries()].sort((a, b) => b[1] - a[1]);
  const total = rows.reduce((a, [, b]) => a + b, 0) || 1;
  const colors = ['#127089', '#2d5fa6', '#1f7a52', '#8f5b00', '#6a4c93', '#4f6a77', '#7a5a6f', '#357a6f', '#6a6f4f', '#45627a'];
  const bar = h('div.stackbar');
  rows.forEach(([, b], i) => { const seg = h('i'); seg.style.background = colors[i % colors.length]; seg.style.flex = String(b / total); bar.appendChild(seg); });
  return h('div.card.card-pad',
    h('div.card-head', icon('layers'), h('h2', t('byKind'))),
    rows.length ? bar : h('p.muted', t('noFindings')),
    h('div.cat-list', rows.slice(0, 8).map(([c, b], i) => {
      const d = h('i.dot'); d.style.background = colors[i % colors.length];
      return h('div.cat-item', d, h('span.c-name', catName(c)), h('span.c-val', size(b)));
    })));
}

function winsCard(list) {
  const top = list.filter(f => f.action === 'quarantine' && f.risk !== 'review').sort((a, b) => b.bytes - a.bytes).slice(0, 5);
  if (!top.length) return null;
  const card = h('div.card.card-pad.wins',
    h('div.card-head', icon('bolt'), h('h2', t('wins')), h('span.muted', t('winsSub'))),
    h('div.win-list', top.map(f => h('button.win', { onclick: () => ctxRef.go('suggestions', { risk: f.risk }) },
      catIcon(f.category),
      h('span.w-main', h('b', f.title), h('span', f.items[0]?.path || '')),
      h('span.chip.' + f.risk, riskName(f.risk)),
      h('b.w-size', size(f.bytes))))));
  card.style.marginTop = '18px';
  return card;
}

function whereCard(tree) {
  const kids = tree.children.slice(0, 8);
  const max = kids[0]?.bytes || 1;
  return h('div.card.card-pad',
    h('div.card-head', icon('folder'), h('h2', t('where'))),
    h('div.bars', kids.map(k => h('button.bar-row', { onclick: () => ctxRef.go('explorer', { path: k.path }), title: k.path },
      h('span.b-lab', k.name), barWithGain(k.bytes / max, k.suggestedBytes / max), h('span.b-val', size(k.bytes))))),
    tree.otherBytes > 0 ? h('p.muted', { class: 'small-note' }, `${t('filesHere')}: ${size(tree.otherBytes)}`) : null);
}

function barWithGain(frac, gainFrac) {
  const m = meter(frac);
  if (gainFrac > 0) {
    const g = h('i.over');
    m.appendChild(g);
    requestAnimationFrame(() => setWidth(g, gainFrac));
  }
  return m;
}

function growthCard(diff) {
  const entries = (diff.entries || []).filter(e => e.delta > 0).slice(0, 6);
  const days = diff.fromAt ? Math.max(1, Math.round((new Date(diff.toAt) - new Date(diff.fromAt)) / 86400000)) : 0;
  const max = entries[0]?.delta || 1;
  return h('div.card.card-pad',
    h('div.card-head', icon('trend'), h('h2', diff.from ? t('growth', days) : t('growth', '–').replace(/\s*\(.*\)/, ''))),
    !diff.from ? h('p.muted', t('growthNone'))
      : entries.length === 0 ? h('p.muted', t('growthFree', signed(diff.freeBytesDelta)))
        : h('div',
          h('div.bars', entries.map((e, i) => h('button.bar-row', { onclick: () => ctxRef.go('explorer', { path: e.path }), title: e.path },
            h('span.b-lab', e.path), meter(e.delta / max, i === 0 ? 'accent' : ''), h('span.b-val', signed(e.delta))))),
          h('p.muted', { class: 'small-note' }, t('growthFree', signed(diff.freeBytesDelta)))));
}

function notes(f, scan) {
  const out = h('div.stack');
  out.style.marginTop = '18px';
  const unseen = f.findings.find(x => x.ruleId === 'system.unaccounted');
  if (unseen && unseen.bytes > (1 << 30)) out.appendChild(h('div.note', icon('info'), h('div', t('unseen', size(unseen.bytes)))));
  const used = f.totalBytes - f.freeBytes;
  if (scan.root.length === 3 && scan.scannedBytes - used > (1 << 30)) out.appendChild(h('div.note', icon('info'), h('div', t('hardlinks', size(scan.scannedBytes - used)))));
  return out.children.length ? out : null;
}

// ---- welcome & scanning ----

function welcome() {
  const drives = store.state.drives;
  return h('div.welcome',
    brandMark(),
    h('h1', t('welcomeTitle')),
    h('p', t('welcomeText')),
    h('div.drive-cards', drives.map(d => {
      const used = d.totalBytes - d.freeBytes;
      return h('button.drive-card', { onclick: () => { selectRoot(d.root); ctxRef.startScan(d.root); } },
        h('div.row-flex', icon('drive'), h('div', h('div.dc-name', d.label ? `${d.label} (${d.root.slice(0, 2)})` : d.root.slice(0, 2)),
          h('div.dc-sub', t('freeOf', size(d.freeBytes), size(d.totalBytes)))), h('span.sp'),
          h('span.btn.btn-primary.btn-sm', icon('scan'), t('scanNow'))),
        meter(d.totalBytes ? used / d.totalBytes : 0, used / d.totalBytes > 0.9 ? 'review' : ''),
        d.canQuarantine ? null : h('div.dc-sub', t('noQuarantine')));
    })),
    h('div.promises',
      promise('undo', t('promiseUndo'), t('promiseUndoText')),
      promise('shield', t('promiseExplain'), t('promiseExplainText')),
      promise('lock', t('promiseLocal'), t('promiseLocalText'))));
}

const promise = (ic, title, text) => h('div.promise', h('b', icon(ic), title), text);

function scanning(job) {
  const el = h('div.card.scanning',
    h('div.pulse', icon('scan')),
    h('div', { class: 'grow' },
      h('h2.page-title', t('scanningTitle', job.target || '')),
      h('p.page-lede', t('scanningText')),
      h('div.scan-stats',
        stat('files', count(job.files), t('statFiles')),
        stat('dirs', count(job.dirs), t('statFolders')),
        stat('bytes', size(job.bytes), t('statFound')),
        stat('elapsed', '0:00', t('statElapsed'))),
      h('div.scan-path', { dataset: { k: 'current' } }, job.current || ''),
      expected(job) ? h('div.determinate', h('i', { dataset: { k: 'bar' } })) : h('div.indeterminate'),
      h('div.row-flex', { class: 'mt2' }, h('button.btn', { onclick: () => post('/api/cancel') }, icon('stop'), t('cancel')))));
  el.querySelector('.grow').style.flex = '1';
  el.querySelector('.grow').style.minWidth = '0';
  el.querySelector('.mt2').style.marginTop = '16px';
  const started = new Date(job.startedAt).getTime();
  const tick = () => {
    const s = Math.max(0, Math.round((Date.now() - started) / 1000));
    const e = el.querySelector('[data-k="elapsed"]');
    if (e) e.textContent = `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  };
  tick();
  elapsedTimer = setInterval(tick, 1000);
  return el;
}

const stat = (k, v, label) => h('div', h('b', { dataset: { k } }, v), h('span', label));

/** Bytes the scan should find: the used space of the drive (only for a whole drive). */
function expected(job) {
  const root = (job.target || '').toLowerCase();
  if (root.length !== 3) return 0;
  const d = store.state?.drives.find(x => x.root.toLowerCase() === root);
  return d ? d.totalBytes - d.freeBytes : 0;
}

function updateScan(j) {
  const total = expected(j), bar = box.querySelector('[data-k="bar"]');
  if (bar && total) bar.style.width = Math.min(97, j.bytes / total * 100) + '%';
  const set = (k, v) => { const e = box.querySelector(`[data-k="${k}"]`); if (e) e.textContent = v; };
  set('files', count(j.files)); set('dirs', count(j.dirs)); set('bytes', size(j.bytes)); set('current', j.current || '');
}
