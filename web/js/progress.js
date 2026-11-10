// Live progress of the running quarantine / restore / delete job (count, bytes, current path), fed by job events.
import { h, size, count, setWidth, dialog } from './ui.js';
import { t } from './i18n.js';
import { store, subscribe } from './store.js';

/** A progress block that follows store.state.job. Call stop() when done with it. */
export function progressView() {
  const ind = h('div.indeterminate');
  const fill = h('i.accent');
  const bar = h('span.meter.prog-bar', fill);
  const nums = h('div.prog-nums', t('progPreparing'));
  const cur = h('div.prog-path');
  bar.hidden = true;
  const el = h('div.progress', ind, bar, nums, cur);
  const update = () => {
    const j = store.state?.job;
    if (!j || j.state !== 'running' || !j.total) return;
    ind.hidden = true;
    bar.hidden = false;
    // Planned bytes say more than the count when one item is far bigger than the rest.
    setWidth(fill, j.totalBytes ? j.doneBytes / j.totalBytes : j.done / j.total);
    nums.textContent = t('progCount', count(j.done), count(j.total)) + ' · ' + size(j.doneBytes) + ' / ' + size(j.totalBytes);
    cur.textContent = j.current || '';
  };
  update();
  const stop = subscribe(what => { if (what === 'job') update(); });
  return { el, stop };
}

/** Shows a dialog that can't be dismissed while work() runs, then closes it and returns work()'s result. */
export async function withProgress({ icon = 'box', title, text }, work) {
  const view = progressView();
  let close;
  dialog(c => { close = c; return { icon, title, text, locked: true, body: view.el }; });
  try { return await work(); }
  finally { view.stop(); close?.(); }
}
