// Apps & games: what is installed and how big, removed the right way (own uninstaller or the launcher).
import { h, clear, size, toast, dialog, skeleton, count, meter, tip } from '../ui.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { currentScan } from '../store.js';
import { get, post } from '../api.js';

let box, ctxRef, apps = [], tab = 'apps', q = '';

export async function render(container, ctx) {
  box = container; ctxRef = ctx;
  tab = ctx.params.get('tab') || tab;
  box.appendChild(skeleton(8));
  const scan = currentScan();
  apps = await get('/api/apps' + (scan ? '?scan=' + encodeURIComponent(scan.id) : ''));
  if (!ctx.isCurrent()) return;
  draw();
}

function draw() {
  clear(box);
  const games = apps.filter(a => a.isGame), plain = apps.filter(a => !a.isGame);
  const search = h('input', { type: 'search', placeholder: t('searchPh'), value: q, 'aria-label': t('search') });
  search.addEventListener('input', () => { q = search.value.trim().toLowerCase(); drawList(); });
  box.append(
    h('div.page-head',
      h('div', h('div.eyebrow', t('navApps')), h('h1.page-title', t('apTitle'), tip(t('apLede')))),
      h('button.btn', { onclick: () => post('/api/apps/settings') }, icon('external'), t('apSettings'))),
    h('div.toolbar',
      h('div.tabs', { role: 'tablist' },
        h('button.tab', { role: 'tab', class: tab === 'apps' ? 'on' : '', onclick: () => { tab = 'apps'; draw(); } }, t('apApps'), h('span.count', count(plain.length))),
        h('button.tab', { role: 'tab', class: tab === 'games' ? 'on' : '', onclick: () => { tab = 'games'; draw(); } }, t('apGames'), h('span.count', count(games.length)))),
      h('span.sp'),
      h('label.input', icon('search'), search)),
    h('div.card.tbl-wrap', { id: 'appList' }),
    h('p.muted', { class: 'small-note' }, t('apNote')));
  drawList();
}

function drawList() {
  const wrap = box.querySelector('#appList');
  const rows = apps.filter(a => (tab === 'games') === a.isGame)
    .filter(a => !q || (a.name + ' ' + (a.publisher || '') + ' ' + (a.location || '')).toLowerCase().includes(q));
  if (!rows.length) { clear(wrap).appendChild(h('div.empty', icon(tab === 'games' ? 'game' : 'apps'), h('h3', t('noMatch')))); return; }
  const max = Math.max(...rows.map(a => a.bytes || 0)) || 1;
  clear(wrap).appendChild(h('table.table',
    h('thead', h('tr', h('th', t('name')), h('th.r', t('size')), h('th.w-bar.hide-sm', t('share')), h('th.hide-sm', t('apPublisher')), h('th'))),
    h('tbody', rows.slice(0, 600).map(a => h('tr',
      h('td', h('div.name', icon(a.isGame ? 'game' : 'apps'), h('span', a.name), a.launcher ? h('span.chip.neutral', a.launcher) : null),
        a.location ? h('div.sub', a.location) : (a.version ? h('div.sub', a.version) : null)),
      h('td.r.num', a.bytes ? size(a.bytes) : '–', a.bytesSource === 'estimate' ? h('span.faint', ' ~') : null),
      h('td.w-bar.hide-sm', a.bytes ? meter(a.bytes / max) : null),
      h('td.hide-sm.muted', a.publisher || ''),
      h('td.r', a.canUninstall ? h('button.btn.btn-sm', { onclick: () => uninstall(a) }, icon('trash'), a.launcher === 'steam' ? t('apSteamRemove') : t('apUninstall')) : null))))));
}

async function uninstall(a) {
  const ok = await dialog(() => ({
    icon: 'trash', title: t('apConfirmTitle', a.name),
    text: a.launcher === 'steam' ? t('apConfirmSteam') : t('apConfirmText'),
    actions: [{ label: t('cancel'), kind: 'ghost', value: false }, { label: a.launcher === 'steam' ? t('apSteamRemove') : t('apUninstall'), kind: 'primary', value: true }],
  }));
  if (!ok) return;
  const r = await post('/api/apps/uninstall', { id: a.id }).catch(e => ({ ok: false, message: e.message }));
  toast(r.ok ? t('apStarted') : t('apFailed'), r.ok ? 'ok' : 'warn');
}
