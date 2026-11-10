// Claude about one suggestion: a side drawer with quick and deep analysis and free questions (one session per finding).
import { h, clear, size, toast } from './ui.js';
import { icon } from './icons.js';
import { t } from './i18n.js';
import { subscribe, currentScan, pref } from './store.js';
import { get, post, ApiError } from './api.js';
import { verdictChip } from './views/analysis.js';

let open = null; // { finding, dlg, msgs, ... } of the drawer that is showing

/** Opens the drawer for finding f; go(page, params) navigates (for the Analysis page link). */
export async function openChat(f, go) {
  open?.close();
  const st = { finding: f, running: false, busyWith: null, aiText: null, steps: new Map() };
  open = st;

  const msgs = h('div.chat-msgs', { 'aria-live': 'polite' });
  const input = h('textarea.chat-input', { rows: 2, placeholder: t('chAskPlaceholder'), 'aria-label': t('chAsk'), maxlength: 2000 });
  const send = h('button.btn.btn-primary', { 'aria-label': t('chSend'), title: t('chSend'), onclick: () => ask() }, icon('send'));
  const stop = h('button.btn', { onclick: () => post('/api/chat/stop').catch(() => {}) }, icon('stop'), t('anStop'));
  const quick = h('button.btn.btn-sm', { onclick: () => run('quick') }, icon('bolt'), t('chQuick'));
  const deep = h('button.btn.btn-sm', { onclick: () => run('deep') }, icon('search'), t('chDeep'));
  const model = h('select.select.select-sm', { 'aria-label': t('anModel') },
    [['', t('anModelDefault')], ['haiku', 'Haiku'], ['sonnet', 'Sonnet'], ['opus', 'Opus']].map(([v, l]) => h('option', { value: v, text: l, selected: pref.get('model', '') === v })));
  model.addEventListener('change', () => pref.set('model', model.value));
  const status = h('div.chat-status');
  input.addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); ask(); } });

  const top = f.items.filter(i => !i.nestedIn);
  const dlg = h('dialog.drawer', { 'aria-label': t('chTitle') },
    h('div.drawer-head',
      h('div.ico-wrap', icon('robot')),
      h('div.dh-main', h('div.eyebrow', t('chTitle')), h('h2', f.title),
        h('div.muted.small', size(f.bytes) + (top[0] ? ' · ' + top[0].path + (top.length > 1 ? ` (+${top.length - 1})` : '') : ''))),
      h('button.icon-btn', { 'aria-label': t('close'), title: t('close'), onclick: () => st.close() }, icon('x'))),
    h('div.chat-quick', quick, deep, h('span.sp'), model),
    msgs,
    status,
    h('div.chat-compose', input, send, stop));
  st.dlg = dlg; st.msgs = msgs; st.status = status;
  const unsub = subscribe((what, data) => { if (what === 'chat' && data.findingId === f.id) onEvent(data); });
  st.close = () => { unsub(); if (open === st) open = null; dlg.close(); dlg.remove(); };
  dlg.addEventListener('cancel', e => { e.preventDefault(); st.close(); });
  dlg.addEventListener('mousedown', e => { if (e.target === dlg) st.close(); }); // click on the backdrop
  document.body.appendChild(dlg);
  dlg.showModal();

  const controls = () => {
    const blocked = st.running || !!st.busyWith || !st.ready;
    for (const b of [quick, deep, send]) b.disabled = blocked;
    input.disabled = !st.ready;
    stop.hidden = !st.running;
    send.hidden = st.running;
    clear(status);
    if (st.running) status.append(h('span.spinner'), t('chThinking'));
    else if (st.busyWith) status.append(icon('clock'), t('chBusy'));
  };

  async function run(mode, text) {
    const scan = currentScan();
    if (!scan) return;
    // Running before the request: a run that fails at once can send its "done" event before the POST answers.
    st.running = true; st.busyWith = null; controls();
    try {
      await post('/api/chat/send', { scan: scan.id, findingId: f.id, mode, text: text || null, model: model.value || null });
      return true;
    } catch (e) {
      st.running = false; controls();
      toast(e instanceof ApiError ? e.message : t('errorGeneric', e.message), 'warn');
      return false;
    }
  }

  async function ask() {
    const text = input.value.trim();
    if (!text || st.running) return;
    if (await run('ask', text)) input.value = '';
  }

  function onEvent(data) {
    if (data.done) { st.running = false; st.aiText = null; controls(); return; }
    st.running = true; controls();
    add(data.event, true);
  }

  function add(ev, live) {
    switch (ev.kind) {
      case 'user':
        msgs.appendChild(h('div.msg.me', ev.subtype && ev.subtype !== 'ask' ? icon(ev.subtype === 'deep' ? 'search' : 'bolt') : null, h('span', ev.text || '')));
        st.aiText = null;
        break;
      case 'text':
        if (!st.aiText) { st.aiText = h('div.msg-text'); st.aiRaw = ''; msgs.appendChild(h('div.msg.ai', icon('robot'), st.aiText)); }
        st.aiRaw += ev.text || '';
        rich(st.aiText, st.aiRaw);
        break;
      case 'tool': {
        if (ev.tool === 'add_note' || ev.tool === 'add_notes') break;
        const label = t('tool_' + ev.tool) === 'tool_' + ev.tool ? ev.tool : t('tool_' + ev.tool);
        const step = h('div.msg-step', h('span.spinner'), h('span', label), h('code.small', argText(ev.input)));
        st.steps.set(ev.toolId, step);
        msgs.appendChild(step);
        st.aiText = null;
        break;
      }
      case 'toolResult': {
        const step = st.steps.get(ev.toolId);
        step?.querySelector('.spinner')?.replaceWith(icon(ev.isError ? 'xCircle' : 'check'));
        break;
      }
      case 'note':
        msgs.appendChild(h('div.note-card', verdictChip(ev.verdict), h('div', h('b', t('chNoteSaved')), h('p', ev.text || ''))));
        st.aiText = null;
        break;
      case 'result':
        if (ev.subtype && ev.subtype !== 'success') msgs.appendChild(h('div.msg-step.warn', icon('warn'), h('span', ev.subtype === 'error_max_turns' ? t('chMaxTurns') : t('anFailed', ev.subtype))));
        break;
      case 'error':
        msgs.appendChild(h('div.msg-step.bad', icon('xCircle'), h('span', ev.text || '')));
        break;
      default: break;
    }
    if (live) msgs.scrollTop = msgs.scrollHeight;
  }

  // History of this finding's chat (kept by the server while Ferahla runs), and whether Claude is usable.
  msgs.appendChild(h('div.chat-hint', t('chLoading')));
  controls();
  try {
    const s = await get('/api/chat?finding=' + encodeURIComponent(f.id));
    if (open !== st) return;
    clear(msgs);
    const c = s.claude;
    if (!c.installed || !c.loggedIn) {
      msgs.appendChild(h('div.chat-hint', icon('info'), h('p', c.installed ? t('anNotLoggedIn') : t('anNotInstalled')),
        h('button.btn.btn-sm', { onclick: () => { st.close(); go('analysis'); } }, icon('robot'), t('chSetUp'))));
      return;
    }
    st.ready = true;
    st.running = s.running;
    st.busyWith = s.busyWith && s.busyWith !== f.id ? s.busyWith : null;
    if (f.note) msgs.appendChild(h('div.note-card', verdictChip(f.noteVerdict), h('div', h('b', f.noteSource === 'claude' ? t('claudeSays') : t('yourNote')), h('p', f.note))));
    if (!s.events.length) msgs.appendChild(h('div.chat-hint', icon('sparkle'), h('p', t('chIntro'))));
    for (const ev of s.events) add(ev, false);
    msgs.scrollTop = msgs.scrollHeight;
    controls();
    input.focus();
  } catch (e) {
    clear(msgs).appendChild(h('div.msg-step.bad', icon('xCircle'), h('span', t('errorGeneric', e.message))));
  }
}

/** `code` and **bold**, as elements (never HTML); everything else stays plain text. */
function rich(el, raw) {
  clear(el);
  for (const part of raw.split(/(`[^`\n]+`|\*\*[^*\n]+\*\*)/)) {
    if (!part) continue;
    if (part.length > 2 && part.startsWith('`') && part.endsWith('`')) el.appendChild(h('code', part.slice(1, -1)));
    else if (part.length > 4 && part.startsWith('**') && part.endsWith('**')) el.appendChild(h('b', part.slice(2, -2)));
    else el.appendChild(document.createTextNode(part));
  }
}

function argText(input) {
  try {
    const o = JSON.parse(input || '{}');
    const v = o.path || o.findingId || '';
    const s = String(v);
    return s.length > 60 ? '…' + s.slice(-59) : s;
  } catch { return ''; }
}
