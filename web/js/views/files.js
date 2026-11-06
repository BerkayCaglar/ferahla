import { h, clear, size, shortDate, toast, skeleton, count } from '../ui.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { cached, currentScan } from '../store.js';
import { post } from '../api.js';

const KINDS = [['all', 'kindAll'], ['big', 'kindBig'], ['installer', 'kindInstaller'], ['archive', 'kindArchive'], ['partial', 'kindPartial']];
let box, ctxRef, kind = 'all', q = '', sort = { key: 'bytes', dir: -1 }, data;

export async function render(container, ctx) {
  box = container; ctxRef = ctx;
  if (!currentScan()) { ctx.go('overview'); return; }
  kind = ctx.params.get('kind') || kind;
  box.appendChild(skeleton(8));
  await load();
}

async function load() {
  const scan = currentScan();
  data = await cached(`/api/files?scan=${scan.id}&kind=${kind}`);
  if (!ctxRef.isCurrent()) return;
  draw();
}

function draw() {
  clear(box);
  const search = h('input', { type: 'search', placeholder: t('searchPh'), value: q, 'aria-label': t('search') });
  search.addEventListener('input', () => { q = search.value.trim().toLowerCase(); drawTable(); });
  box.append(
    h('div.page-head', h('div', h('div.eyebrow', currentScan().root), h('h1.page-title', t('flTitle')), h('p.page-lede', t('flLede')))),
    h('div.toolbar',
      h('div.tabs', { role: 'tablist' }, KINDS.map(([k, label]) => h('button.tab', {
        role: 'tab', class: kind === k ? 'on' : '', 'aria-selected': String(kind === k),
        onclick: () => { kind = k; box.querySelector('.tbl-wrap')?.replaceChildren(skeleton(4)); load(); },
      }, t(label)))),
      h('span.sp'),
      h('span.muted.num', `${count(data.total)} · ${size(data.totalBytes)}`),
      h('label.input', icon('search'), search)),
    h('div.card.tbl-wrap'));
  drawTable();
}

function drawTable() {
  const wrap = box.querySelector('.tbl-wrap');
  let rows = data.items.filter(f => !q || f.path.toLowerCase().includes(q));
  const k = sort.key;
  rows = rows.sort((a, b) => (k === 'name' ? b.name.localeCompare(a.name) : k === 'lastWriteUtc' ? new Date(a[k]) - new Date(b[k]) : a[k] - b[k]) * sort.dir);
  const th = (key, label, cls = '') => h('th' + (cls ? '.' + cls : ''), h('button', {
    onclick: () => { sort = { key, dir: sort.key === key ? -sort.dir : -1 }; drawTable(); },
  }, label, sort.key === key ? icon(sort.dir < 0 ? 'chevDown' : 'up') : null));
  if (rows.length === 0) { clear(wrap).appendChild(h('div.empty', icon('file'), h('h3', t('noMatch')))); return; }
  clear(wrap).append(h('table.table',
    h('thead', h('tr', th('name', t('name')), th('bytes', t('size'), 'r'), th('lastWriteUtc', t('modified'), 'hide-sm'), h('th'))),
    h('tbody', rows.slice(0, 500).map(f => h('tr',
      h('td', h('div.name', icon(f.kinds.includes('installer') ? 'installer' : f.kinds.includes('archive') ? 'archive' : f.kinds.includes('partialDownload') ? 'download' : 'file'),
        h('span', f.name),
        f.findingId ? h('span.chip.safe', t('inSuggestions')) : null),
        h('div.sub', f.path)),
      h('td.r.num', size(f.bytes)),
      h('td.hide-sm.muted', shortDate(f.lastWriteUtc)),
      h('td.r', h('button.icon-btn', { title: t('showInExplorer'), 'aria-label': t('showInExplorer'), onclick: () => post('/api/reveal', { path: f.path }).catch(e => toast(e.message, 'error')) }, icon('external'))))))),
    data.total > data.items.length || rows.length > 500 ? h('p.muted', { class: 'small-note pad' }, t('shown', Math.min(rows.length, 500), data.total)) : null);
}
