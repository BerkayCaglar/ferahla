// New releases: a bar over the page when one is out, a dialog with its notes, and the update itself
// (the server downloads and checks the new exe, swaps it in and restarts on the same address; this tab then reloads).
import { h, size, date, ago, dialog, setWidth, toast } from './ui.js';
import { icon } from './icons.js';
import { t } from './i18n.js';
import { store, pref, subscribe, runJob, jobRunning } from './store.js';
import { get, post, ApiError } from './api.js';

const available = () => {
  const u = store.state?.update;
  return u?.enabled ? u.available || null : null;
};

/** The bar shown above the top bar, or null. */
export function updateBar() {
  const a = available();
  if (!a || pref.get('update-dismissed', '') === a.version) return null;
  const bar = h('div.update-bar', { role: 'status' },
    h('span'), // keeps the message centred between equal side columns
    h('div.ub-msg', icon('sparkle'), h('span', t('upReady', a.version))),
    h('div.ub-actions',
      h('button.btn.btn-sm.btn-primary', { onclick: () => openUpdate() }, t('upDetails'), icon('chevron')),
      h('button.icon-btn', {
      'aria-label': t('upLater'), title: t('upLater'),
      onclick: () => { pref.set('update-dismissed', a.version); bar.remove(); },
    }, icon('x'))));
  return bar;
}

/** Download progress of the running update job. */
function downloadView() {
  const fill = h('i.accent');
  const nums = h('div.prog-nums', t('progPreparing'));
  const el = h('div.progress', h('span.meter.prog-bar', fill), nums);
  const update = () => {
    const j = store.state?.job;
    if (!j || j.kind !== 'update' || !j.totalBytes) return;
    setWidth(fill, j.doneBytes / j.totalBytes);
    nums.textContent = j.doneBytes >= j.totalBytes ? t('upVerifying') : size(j.doneBytes) + ' / ' + size(j.totalBytes);
  };
  const stop = subscribe(what => { if (what === 'job') update(); });
  return { el, stop };
}

/** Polls until the restarted Ferahla answers with the new version. */
async function waitForVersion(version) {
  const until = Date.now() + 90_000;
  while (Date.now() < until) {
    await new Promise(r => setTimeout(r, 800));
    try { if ((await get('/api/state')).version === version) return true; } catch { /* still restarting */ }
  }
  return false;
}

export function openUpdate() {
  let phase = 'info', err = null, view = null;
  return dialog((close, render) => {
    const u = store.state?.update || {};
    const a = u.available;
    if (phase === 'installing') return { icon: 'download', title: t('upInstalling', a.version), text: t('upInstallingText'), locked: true, body: view.el };
    if (phase === 'restarting') return { icon: 'sparkle', title: t('upRestarting'), text: t('upRestartingText'), locked: true, body: h('div.progress', h('div.indeterminate')) };
    if (phase === 'lost') return { icon: 'warn', title: t('upLost'), text: t('upLostText'), actions: [{ label: t('close'), kind: 'primary', value: false }] };
    if (!a) return { icon: 'checkCircle', title: t('upNone'), actions: [{ label: t('close'), kind: 'primary', value: false }] };

    const install = async () => {
      phase = 'installing'; err = null; view = downloadView(); render();
      store.restarting = true; // the server goes away on purpose: no "lost connection" toast
      try {
        const job = await runJob('/api/update/install', {});
        view.stop();
        if (job.state !== 'done') throw new Error(job.error || t('upFailed'));
        phase = 'restarting'; render();
        if (await waitForVersion(a.version)) location.reload();
        else { phase = 'lost'; render(); }
      } catch (e) {
        view.stop();
        store.restarting = false;
        phase = 'info'; err = e instanceof ApiError || e instanceof Error ? e.message : String(e); render();
      }
    };

    return {
      icon: 'sparkle', title: t('upReady', a.version),
      text: t('upYouHave', u.current) + (a.publishedAt ? ' · ' + date(a.publishedAt, false) : '') + (a.size ? ' · ' + size(a.size) : ''),
      body: h('div.up-body',
        a.notes ? h('div.up-notes', a.notes) : null,
        u.canInstall ? h('p.muted.small', icon('shield'), t('upHowText')) : h('p.muted.small', icon('info'), t('upCantInstall')),
        err ? h('p.up-err', icon('xCircle'), h('span', err)) : null),
      actions: [
        { label: t('upLater'), kind: 'ghost', value: false },
        { label: t('upGitHub'), run: () => window.open(a.url, '_blank', 'noopener,noreferrer') },
        u.canInstall ? { label: t('upNow'), kind: 'primary', disabled: jobRunning(), run: install } : null,
      ].filter(Boolean),
    };
  });
}

/**
 * Asks GitHub now. With an update: opens its dialog (whatever was dismissed before). Without: says so.
 * Returns true when an update was found.
 */
export async function checkNow(btn) {
  if (btn) btn.disabled = true;
  try {
    const u = await post('/api/update/check', { message: 'now' });
    if (store.state) store.state.update = u;
    if (!u.enabled) { toast(t('upOff')); return false; }
    if (u.available) { pref.set('update-dismissed', ''); openUpdate(); return true; }
    toast(u.checkedAt ? t('upNone') : t('upUnreachable'), u.checkedAt ? 'ok' : 'warn');
    return false;
  } catch (e) {
    toast(t('errorGeneric', e.message), 'error');
    return false;
  } finally { if (btn) btn.disabled = false; }
}

/** Version, the update switch, and "check now" (from the version in the sidebar). */
export function openAbout() {
  let busy = false;
  return dialog((close, render) => {
    const u = store.state?.update || {};
    const sw = h('input', { type: 'checkbox', checked: !!u.enabled, disabled: busy });
    sw.addEventListener('change', async () => {
      busy = true; render();
      try { store.state.update = await post('/api/update/settings', { enabled: sw.checked }); }
      catch (e) { toast(t('errorGeneric', e.message), 'error'); }
      busy = false; render();
    });
    const status = !u.enabled ? t('upOff')
      : u.available ? t('upReady', u.available.version)
      : u.checkedAt ? t('upLatest', ago(u.checkedAt)) : t('upNotYet');
    return {
      icon: 'sparkle', title: 'Ferahla ' + (u.current || store.state?.version || ''),
      body: h('div.up-body',
        h('label.check-row', sw, h('span', h('b', t('upAuto')), h('span.muted.small', t('upAutoText')))),
        h('p.small', u.available ? icon('sparkle') : icon('checkCircle'), h('span', status))),
      actions: [
        { label: t('close'), kind: 'ghost', value: false },
        u.available
          ? { label: t('upDetails'), kind: 'primary', run: c => { c(false); openUpdate(); } }
          : { label: t('upCheck'), disabled: !u.enabled || busy, run: async c => {
              busy = true; render();
              const found = await checkNow();
              busy = false;
              if (found) c(false); else render();
            } },
      ],
    };
  });
}

/** Asks the server to look again when the window gets focus (it checks at most every 2 minutes). */
export function checkOnFocus() {
  const ping = () => { if (document.visibilityState === 'visible' && store.state?.update?.enabled) post('/api/update/check').catch(() => {}); };
  window.addEventListener('focus', ping);
  document.addEventListener('visibilitychange', ping);
}
