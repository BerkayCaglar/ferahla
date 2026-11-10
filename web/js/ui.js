// DOM helpers, formatting, dialogs, toasts. Text always goes in as text nodes (never innerHTML).
import { icon } from './icons.js';
import { t, getLang } from './i18n.js';

/** h('div.card.pad', {onclick}, child, 'text', [more]) */
export function h(spec, attrs, ...kids) {
  if (attrs === null || typeof attrs !== 'object' || attrs instanceof Node || Array.isArray(attrs)) { kids.unshift(attrs); attrs = {}; }
  const [tag, ...classes] = spec.split('.');
  const el = document.createElement(tag || 'div');
  if (classes.length) el.className = classes.join(' ');
  for (const [k, v] of Object.entries(attrs)) {
    if (v === undefined || v === null || v === false) continue;
    if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    else if (k === 'class') el.className = (el.className + ' ' + v).trim();
    else if (k === 'text') el.textContent = v;
    else if (k === 'dataset') Object.assign(el.dataset, v);
    else if (k in el && typeof v !== 'string') el[k] = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  append(el, kids);
  return el;
}

function append(el, kids) {
  for (const k of kids) {
    if (k === null || k === undefined || k === false) continue;
    if (Array.isArray(k)) append(el, k);
    else el.appendChild(k instanceof Node ? k : document.createTextNode(String(k)));
  }
}

export const $ = (sel, root = document) => root.querySelector(sel);
export const clear = el => { while (el.firstChild) el.removeChild(el.firstChild); return el; };

/** A meter whose fill width is set through the CSSOM (allowed by CSP, unlike style attributes). */
export function meter(fraction, cls = '') {
  const fill = h('i' + (cls ? '.' + cls : ''));
  const m = h('span.meter', fill);
  requestAnimationFrame(() => { fill.style.width = Math.max(0, Math.min(1, fraction || 0)) * 100 + '%'; });
  return m;
}

export function setWidth(el, fraction) { el.style.width = Math.max(0, Math.min(1, fraction || 0)) * 100 + '%'; }

// ---- formatting (binary units, like the CLI) ----

const nf = (min, max) => new Intl.NumberFormat(getLang(), { minimumFractionDigits: min, maximumFractionDigits: max });

export function size(bytes) {
  if (bytes === null || bytes === undefined || isNaN(bytes)) return '–';
  const neg = bytes < 0;
  let b = Math.abs(bytes);
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let u = 0;
  while (b >= 1024 && u < units.length - 1) { b /= 1024; u++; }
  const s = u === 0 ? nf(0, 0).format(b) : (b >= 100 ? nf(0, 0) : nf(1, 1)).format(b);
  return (neg ? '-' : '') + s + ' ' + units[u];
}

export const signed = b => (b > 0 ? '+' : '') + size(b);
export const count = n => nf(0, 0).format(n || 0);
export const pct = f => nf(0, f < 0.1 ? 1 : 0).format((f || 0) * 100) + '%';

export function ago(iso) {
  if (!iso) return '';
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 90) return t('ago_now');
  if (s < 3600) return t('ago_min', Math.round(s / 60));
  if (s < 86400 * 1.5) return t('ago_h', Math.round(s / 3600));
  return t('ago_d', Math.round(s / 86400));
}

export function date(iso, withTime = true) {
  if (!iso) return '';
  const d = new Date(iso);
  return new Intl.DateTimeFormat(getLang(), withTime ? { dateStyle: 'medium', timeStyle: 'short' } : { dateStyle: 'medium' }).format(d);
}

export function shortDate(iso) {
  if (!iso) return '–';
  return new Intl.DateTimeFormat(getLang(), { day: 'numeric', month: 'short', year: '2-digit' }).format(new Date(iso));
}

// ---- toasts ----

export function toast(text, kind = 'info', action) {
  let host = $('.toasts');
  if (!host) { host = h('div.toasts', { role: 'status', 'aria-live': 'polite' }); document.body.appendChild(host); }
  const ico = kind === 'ok' ? 'checkCircle' : kind === 'error' ? 'xCircle' : kind === 'warn' ? 'warn' : 'info';
  const el = h('div.toast', icon(ico), h('div', text));
  if (action) el.appendChild(h('button', { text: action.label, onclick: () => { action.run(); dismiss(); } }));
  host.appendChild(el);
  const dismiss = () => { el.classList.add('out'); setTimeout(() => el.remove(), 220); };
  setTimeout(dismiss, action ? 9000 : 5200);
}

// ---- dialogs ----

/**
 * Opens a modal <dialog>. build(close) returns {icon, tone, title, text, body, actions:[{label, kind, run}]}.
 * Resolves with the value passed to close().
 */
export function dialog(build) {
  return new Promise(resolve => {
    const dlg = h('dialog');
    let done = false, locked = false;
    const close = v => { if (done) return; done = true; dlg.close(); dlg.remove(); resolve(v); };
    const render = () => {
      const spec = build(close, render);
      locked = !!spec.locked; // Esc does nothing while work is running
      // append() skips null parts; the native Element.append(null) would insert the text "null".
      append(clear(dlg), [
        h('div.dlg-head',
          spec.icon ? h('div.ico-wrap' + (spec.tone === 'danger' ? '.danger' : ''), icon(spec.icon)) : null,
          h('div', h('h2', spec.title), spec.text ? h('p', spec.text) : null)),
        spec.body ? h('div.dlg-body', spec.body) : null,
        spec.actions?.length ? h('div.dlg-foot', spec.left || null, spec.left ? h('span.sp') : null,
          spec.actions.map(a => h('button.btn' + (a.kind ? '.btn-' + a.kind : ''), {
            text: a.label, disabled: a.disabled, onclick: () => a.run ? a.run(close) : close(a.value),
          }))) : null]);
    };
    render();
    dlg.addEventListener('cancel', e => { e.preventDefault(); if (!locked) close(undefined); });
    document.body.appendChild(dlg);
    dlg.showModal();
  });
}

export async function confirm(title, text, okLabel, tone) {
  return dialog(close => ({
    icon: tone === 'danger' ? 'warn' : 'info', tone, title, text,
    actions: [{ label: t('cancel'), kind: 'ghost', value: false }, { label: okLabel, kind: tone === 'danger' ? 'danger' : 'primary', value: true }],
  }));
}

/** Asks for one line of text. Resolves with the text, or undefined if cancelled. */
export function promptDialog({ icon: ic = 'folder', title, text, label, value = '', placeholder = '', okLabel }) {
  let input;
  const p = dialog(close => {
    input = h('input.text-input', { type: 'text', value, placeholder, 'aria-label': label || title, spellcheck: 'false' });
    input.addEventListener('keydown', e => { if (e.key === 'Enter' && input.value.trim()) close(input.value.trim()); });
    return {
      icon: ic, title, text,
      body: h('label.field', label ? h('span', label) : null, input),
      actions: [{ label: t('cancel'), kind: 'ghost', value: undefined }, { label: okLabel || 'OK', kind: 'primary', run: c => input.value.trim() && c(input.value.trim()) }],
    };
  });
  setTimeout(() => { input?.focus(); input?.select(); }, 30);
  return p;
}

/** A small popup menu anchored under `anchor`; items: [{icon, title, sub, on, run, extra}]. */
export function menu(anchor, items) {
  document.querySelectorAll('.menu').forEach(m => m.remove());
  const r = anchor.getBoundingClientRect();
  const m = h('div.menu', { role: 'menu' });
  for (const it of items) {
    if (it === '-') { m.appendChild(h('div.menu-sep')); continue; }
    m.appendChild(h('button.menu-item' + (it.on ? '.on' : ''), { role: 'menuitem', onclick: () => { m.remove(); it.run(); } },
      icon(it.icon || 'drive'), h('span.m-title', it.title), it.right ? h('span.muted', it.right) : h('span'),
      it.sub ? h('span.m-sub', it.sub) : null, it.extra || null));
  }
  document.body.appendChild(m);
  const left = Math.min(r.left + window.scrollX, window.innerWidth - m.offsetWidth - 12);
  m.style.left = Math.max(12, left) + 'px';
  m.style.top = r.bottom + window.scrollY + 6 + 'px';
  const off = e => { if (!m.contains(e.target)) { m.remove(); document.removeEventListener('mousedown', off, true); } };
  setTimeout(() => document.addEventListener('mousedown', off, true));
  m.querySelector('button')?.focus();
  m.addEventListener('keydown', e => {
    if (e.key === 'Escape') { m.remove(); anchor.focus(); }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      const bs = [...m.querySelectorAll('button')];
      const i = bs.indexOf(document.activeElement);
      bs[(i + (e.key === 'ArrowDown' ? 1 : -1) + bs.length) % bs.length].focus();
      e.preventDefault();
    }
  });
}

export async function copyText(text, btn) {
  try {
    await navigator.clipboard.writeText(text);
    if (btn) { const old = btn.textContent; btn.textContent = t('copied'); setTimeout(() => btn.textContent = old, 1400); }
  } catch { toast(text); }
}

export function skeleton(lines = 4) {
  return h('div', h('div.skeleton.sk-block'), ...Array.from({ length: lines }, () => h('div.skeleton.sk-line')));
}

export const riskName = r => t('risk' + r[0].toUpperCase() + r.slice(1));
export const catName = c => t('cat_' + c) === 'cat_' + c ? c : t('cat_' + c);
