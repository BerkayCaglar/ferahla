// Shell: sidebar, top bar, router. Views live in ./views and render into #content.
import { h, $, clear, toast, menu, size, ago, meter, confirm, promptDialog } from './ui.js';
import { icon, brandMark } from './icons.js';
import { t, setLang, getLang } from './i18n.js';
import { hasToken, post, download, ApiError } from './api.js';
import { store, subscribe, refreshState, startEvents, selectRoot, currentScan, scansOf, runJob, jobRunning, pref } from './store.js';
import * as overview from './views/overview.js';
import * as suggestions from './views/suggestions.js';
import * as explorer from './views/explorer.js';
import * as files from './views/files.js';
import * as quarantine from './views/quarantine.js';
import * as history from './views/history.js';
import * as analysis from './views/analysis.js';
import * as apps from './views/apps.js';
import { updateBar, openAbout, checkOnFocus, checkNow } from './update.js';

const ROUTES = [
  { id: 'overview', icon: 'overview', label: 'navOverview', view: overview },
  { id: 'suggestions', icon: 'sparkle', label: 'navSuggestions', view: suggestions },
  { id: 'explorer', icon: 'folder', label: 'navExplorer', view: explorer },
  { id: 'files', icon: 'file', label: 'navFiles', view: files },
  { id: 'quarantine', icon: 'box', label: 'navQuarantine', view: quarantine },
  { id: 'apps', icon: 'apps', label: 'navApps', view: apps },
  { id: 'history', icon: 'history', label: 'navHistory', view: history },
  { id: 'analysis', icon: 'robot', label: 'navAnalysis', view: analysis },
];

let content, topbar, updateSlot, nav, cleanup = null, renderSeq = 0;

// ---- theme ----

function applyTheme(mode) {
  if (mode === 'light' || mode === 'dark') document.documentElement.dataset.theme = mode;
  else delete document.documentElement.dataset.theme;
}

// ---- routing ----

export function route() {
  const [path, query = ''] = location.hash.replace(/^#\/?/, '').split('?');
  const r = ROUTES.find(x => x.id === path) || ROUTES[0];
  return { ...r, params: new URLSearchParams(query) };
}

export function go(id, params) {
  const q = params ? '?' + new URLSearchParams(params).toString() : '';
  const next = '#/' + id + q;
  if (location.hash === next) render(); else location.hash = next;
}

async function render() {
  if (!store.state) return;
  const seq = ++renderSeq;
  const r = route();
  for (const b of nav.querySelectorAll('.nav-item')) { b.classList.toggle('on', b.dataset.id === r.id); if (b.dataset.id === r.id) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current'); }
  if (cleanup) { try { cleanup(); } catch { /* view already gone */ } cleanup = null; }
  clear(content);
  const box = h('div.view');
  content.appendChild(box);
  document.title = 'Ferahla · ' + t(r.label);
  try {
    const c = await r.view.render(box, { params: r.params, go, startScan, scan: currentScan(), rerender: render, isCurrent: () => seq === renderSeq });
    if (seq === renderSeq) cleanup = c || null;
    else if (typeof c === 'function') c();
  } catch (e) {
    if (seq !== renderSeq) return;
    clear(box).appendChild(h('div.note.danger', icon('xCircle'), h('div', t('errorGeneric', e.message || e))));
  }
}

// ---- shell ----

function sidebar() {
  nav = h('nav.nav', { 'aria-label': 'Ferahla' },
    ROUTES.map(r => h('a.nav-item', { href: '#/' + r.id, dataset: { id: r.id } },
      icon(r.icon), h('span.label', t(r.label)),
      r.id === 'quarantine' ? h('span.badge.accent.hidden', { id: 'qBadge' }) : null,
      r.id === 'suggestions' ? h('span.badge.hidden', { id: 'sBadge' }) : null)));

  const themeSeg = h('div.seg', { role: 'group', 'aria-label': 'Theme' },
    [['system', 'monitor', 'themeSystem'], ['light', 'sun', 'themeLight'], ['dark', 'moon', 'themeDark']].map(([mode, ic, label]) =>
      h('button', {
        title: t(label), 'aria-label': t(label), class: pref.get('theme', 'system') === mode ? 'on' : '',
        onclick: e => {
          pref.set('theme', mode); applyTheme(mode);
          for (const b of e.currentTarget.parentElement.children) b.classList.toggle('on', b === e.currentTarget);
        },
      }, icon(ic))));
  const langSeg = h('div.seg', { role: 'group', 'aria-label': 'Language' },
    ['en', 'tr'].map(l => h('button', {
      text: l.toUpperCase(), class: getLang() === l ? 'on' : '',
      onclick: async () => {
        if (getLang() === l) return;
        pref.set('lang', l);
        await post('/api/lang', { lang: l }).catch(() => {});
        setLang(l); boot(true);
      },
    })));

  return h('aside.sidebar',
    h('div.brand', brandMark(), h('div', h('div.brand-name', 'Ferahla'), h('div.brand-tag', t('appTag')))),
    nav,
    h('div.sidebar-foot', h('div.drive-mini', { id: 'driveMini' }), themeSeg, langSeg,
      h('div.version', h('span.ver-group', h('button.ver', { title: t('upAbout'), onclick: () => openAbout() }, 'v' + (store.state?.version || '')),
        h('button.ver-check', { title: t('upCheck'), 'aria-label': t('upCheck'), onclick: e => checkNow(e.currentTarget) }, icon('refresh'))),
        h('button', { text: t('quit'), onclick: quit }))));
}

async function quit() {
  if (!await confirm(t('quit'), t('quitConfirm'), t('quit'))) return;
  await post('/api/quit').catch(() => {});
  document.body.replaceChildren(h('div.gate', h('div.card', brandMark(), h('h1', 'Ferahla'), h('p', t('quitDone')))));
}

function renderUpdate() {
  if (updateSlot) clear(updateSlot).append(updateBar() || '');
}

function driveLabel(root) {
  const d = store.state?.drives.find(x => x.root === root);
  if (d) return (d.label ? `${d.label} (${root.slice(0, 2)})` : root.slice(0, 2));
  return root;
}

function renderTopbar() {
  if (!topbar) return;
  const scan = currentScan();
  const root = store.root;
  const btn = h('button.drive-btn', { 'aria-haspopup': 'menu', onclick: e => driveMenu(e.currentTarget) },
    icon(root && root.length > 3 ? 'folder' : 'drive'),
    h('span', h('span.d-name', driveLabel(root || '')), h('span.d-sub', scan ? t('scanned', ago(scan.scannedAt)) : t('notScanned'))),
    icon('chevDown', 'chev'));

  const job = store.state?.job;
  let jobEl = null;
  if (job?.state === 'running') {
    const label = job.kind === 'scan' ? t('scanningTitle', job.target || '') + ` · ${size(job.bytes)}` : job.kind === 'update' ? t('upInstalling', job.target || '') : t('pvChecking');
    jobEl = h('button.job-pill', { onclick: () => job.kind === 'scan' && go('overview') }, h('span.spinner'), label);
  }

  clear(topbar).append(btn, jobEl || '', h('span.spacer'),
    scan ? h('button.btn.btn-ghost.btn-sm', { title: t('report'), onclick: exportReport }, icon('download'), h('span.hide-sm', t('report'))) : '',
    h('button.btn.btn-primary', { disabled: jobRunning(), onclick: () => startScan(root) }, icon('scan'), scan ? t('rescan') : t('scanNow')));
}

async function exportReport() {
  const scan = currentScan();
  if (!scan) return;
  try { await download('/api/report?scan=' + encodeURIComponent(scan.id), 'ferahla-report.html'); toast(t('reportSaved'), 'ok'); }
  catch (e) { toast(t('errorGeneric', e.message), 'error'); }
}

function driveMenu(anchor) {
  const s = store.state;
  const items = s.drives.map(d => {
    const last = scansOf(d.root)[0];
    const used = d.totalBytes - d.freeBytes;
    return {
      icon: 'drive', on: d.root === store.root, title: driveLabel(d.root), right: size(d.freeBytes),
      sub: (last ? t('scanned', ago(last.scannedAt)) : t('notScanned')) + ' · ' + t('freeOf', size(d.freeBytes), size(d.totalBytes)) + (d.canQuarantine ? '' : ' · ' + t('noQuarantine')),
      extra: meter(d.totalBytes ? used / d.totalBytes : 0, used / d.totalBytes > 0.9 ? 'review' : ''),
      run: () => selectRoot(d.root),
    };
  });
  const folders = [...new Set(s.scans.map(x => x.root))].filter(r => r.length > 3);
  for (const f of folders) items.push({ icon: 'folder', on: f === store.root, title: f, sub: t('scanned', ago(scansOf(f)[0].scannedAt)), run: () => selectRoot(f) });
  items.push('-', {
    icon: 'folder', title: t('scanFolder'), run: async () => {
      const p = await promptDialog({ title: t('scanFolder').replace('…', ''), text: t('scanFolderText'), label: t('scanFolderPrompt'), value: store.root && store.root.length > 3 ? store.root : '', placeholder: 'D:\\Projects', okLabel: t('scanNow') });
      if (p) startScan(p.replace(/[\\/]+$/, '') || p);
    },
  });
  menu(anchor, items);
}

export async function startScan(root) {
  if (jobRunning()) { toast(t('jobBusy'), 'warn'); return; }
  try {
    if (location.hash.replace(/\?.*/, '') !== '#/overview') go('overview');
    const job = await runJob('/api/scan', { root });
    if (job.state === 'done') {
      await refreshState();
      if (job.scan) selectRoot(job.scan.root);
      toast(t('scanDone', job.scan?.root || root, job.scan?.elapsedSeconds ?? ''), 'ok');
    } else if (job.state === 'cancelled') toast(t('cancelled'));
    else toast(t('scanFailed', job.error || ''), 'error');
  } catch (e) {
    toast(e instanceof ApiError && e.status === 409 ? t('jobBusy') : t('errorGeneric', e.message), 'error');
  }
}

function driveMini() {
  const el = $('#driveMini');
  if (!el) return;
  const root = store.root || '';
  const d = store.state?.drives.find(x => x.root.toLowerCase() === root.slice(0, 3).toLowerCase());
  if (!d) { clear(el); return; }
  const used = d.totalBytes - d.freeBytes;
  clear(el).append(h('div.row-flex', icon('drive'), h('b', root.slice(0, 2)), h('span.sp'), h('span.muted', t('freeShort', size(d.freeBytes)))),
    meter(d.totalBytes ? used / d.totalBytes : 0, used / d.totalBytes > 0.9 ? 'review' : 'accent'));
}

function badges() {
  driveMini();
  const q = $('#qBadge');
  if (q) { const n = store.state?.quarantineItems || 0; q.textContent = n; q.classList.toggle('hidden', n === 0); }
}

let booted = false, stopEvents = null;

async function boot(relang = false) {
  const root = $('#app');
  if (!hasToken()) {
    root.replaceChildren(h('div.gate', h('div.card', brandMark(), h('h1', t('gateTitle')), h('p', t('gateText')))));
    return;
  }
  if (!relang) {
    try { await refreshState(); }
    catch (e) {
      root.replaceChildren(h('div.gate', h('div.card', brandMark(), h('h1', t('gateTitle')), h('p', e.status === 401 ? t('gateText') : t('offline')))));
      return;
    }
    const wanted = pref.get('lang', null);
    if (wanted && wanted !== store.state.lang) { await post('/api/lang', { lang: wanted }).catch(() => {}); store.state.lang = wanted; }
    setLang(store.state.lang);
  }
  topbar = h('header.topbar');
  content = h('main.content', { id: 'content' });
  updateSlot = h('div.update-slot');
  root.replaceChildren(h('div.app', sidebar(), h('div.main', updateSlot, topbar, content)));
  renderTopbar(); badges(); renderUpdate();
  render();
  if (booted) return;
  booted = true;
  window.addEventListener('hashchange', render);
  stopEvents = startEvents();
  checkOnFocus();
  let lastScanId = currentScan()?.id, lastRoot = store.root;
  subscribe((what, data) => {
    if (what === 'analysis') { route().view.onEvent?.('analysis', data); return; }
    if (what === 'chat') return; // the chat drawer listens itself
    if (what === 'offline' && !store.restarting) toast(t('offline'), 'warn');
    if (what === 'online' && !store.restarting) toast(t('reconnected'), 'ok');
    if (what === 'update' || what === 'state') renderUpdate();
    if (what === 'update') return;
    renderTopbar(); badges();
    const scanId = currentScan()?.id;
    if (what === 'root' || store.root !== lastRoot || scanId !== lastScanId) {
      lastRoot = store.root; lastScanId = scanId;
      render();
    } else if (what === 'state' || what === 'job') {
      route().view.onEvent?.(what);
    }
  });
}

applyTheme(pref.get('theme', 'system'));
setLang(pref.get('lang', navigator.language?.startsWith('tr') ? 'tr' : 'en'));
window.addEventListener('pagehide', () => stopEvents?.());
// "/" jumps to the page's search box (like most web apps); Escape leaves it.
document.addEventListener('keydown', e => {
  const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName || '');
  if (e.key === '/' && !typing && !document.querySelector('dialog[open]')) {
    const s = document.querySelector('.view input[type=search]');
    if (s) { e.preventDefault(); s.focus(); }
  } else if (e.key === 'Escape' && typing && document.activeElement.type === 'search') document.activeElement.blur();
});
boot();
