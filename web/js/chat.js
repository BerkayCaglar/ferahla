// An AI agent (Claude Code or Codex) about one suggestion: a side drawer with quick and deep analysis and free
// questions (one session per finding and agent; switching the agent starts a new conversation and keeps this one on screen).
import { h, clear, size, toast } from './ui.js';
import { icon } from './icons.js';
import { t } from './i18n.js';
import { subscribe, currentScan, pref } from './store.js';
import { get, post, ApiError } from './api.js';
import { verdictChip } from './views/analysis.js';
import { agentName, isReady, pickAgent, modelSelect, noteAuthor } from './agents.js';

let open = null; // { finding, dlg, msgs, ... } of the drawer that is showing

/** Opens the drawer for finding f; go(page, params) navigates (for the Analysis page link). */
export async function openChat(f, go) {
  open?.close();
  const st = { finding: f, running: false, busyWith: null, aiText: null, steps: new Map(), agents: [], agent: null, lastAgent: null };
  open = st;

  const msgs = h('div.chat-msgs', { 'aria-live': 'polite' });
  const input = h('textarea.chat-input', { rows: 2, placeholder: t('chAskPlaceholder'), 'aria-label': t('chAsk'), maxlength: 2000 });
  const send = h('button.btn.btn-primary', { 'aria-label': t('chSend'), title: t('chSend'), onclick: () => ask() }, icon('send'));
  const stop = h('button.btn', { onclick: () => post('/api/chat/stop').catch(() => {}) }, icon('stop'), t('anStop'));
  const quick = h('button.btn.btn-sm', { onclick: () => run('quick') }, icon('bolt'), t('chQuick'));
  const deep = h('button.btn.btn-sm', { onclick: () => run('deep') }, icon('search'), t('chDeep'));
  const pickers = h('span.chat-pickers');
  let model = null;
  const status = h('div.chat-status');
  const title = h('div.eyebrow', t('chTitle', 'Claude'));
  input.addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); ask(); } });

  const top = f.items.filter(i => !i.nestedIn);
  const dlg = h('dialog.drawer', { 'aria-label': t('chAskClaude') },
    h('div.drawer-head',
      h('div.ico-wrap', icon('robot')),
      h('div.dh-main', title, h('h2', f.title),
        h('div.muted.small', size(f.bytes) + (top[0] ? ' · ' + top[0].path + (top.length > 1 ? ` (+${top.length - 1})` : '') : ''))),
      h('button.icon-btn', { 'aria-label': t('close'), title: t('close'), onclick: () => st.close() }, icon('x'))),
    h('div.chat-quick', quick, deep, h('span.sp'), pickers),
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

  /** Agent and model pickers: the agent one only when more than one agent is ready. */
  function drawPickers() {
    const ready = st.agents.filter(isReady);
    const info = st.agents.find(a => a.id === st.agent);
    title.textContent = t('chTitle', agentName(st.agent));
    clear(pickers);
    if (ready.length > 1) {
      const sel = h('select.select.select-sm', { 'aria-label': t('anAgent') },
        ready.map(a => h('option', { value: a.id, text: a.name, selected: a.id === st.agent })));
      sel.addEventListener('change', () => { st.agent = sel.value; pref.set('agent', sel.value); drawPickers(); controls(); });
      pickers.append(sel);
    }
    model = info ? modelSelect(info, 'select-sm') : null;
    if (model) pickers.append(model);
  }

  const controls = () => {
    const blocked = st.running || !!st.busyWith || !st.ready;
    for (const b of [quick, deep, send]) b.disabled = blocked;
    input.disabled = !st.ready;
    stop.hidden = !st.running;
    send.hidden = st.running;
    clear(status);
    if (st.running) status.append(h('span.spinner'), t('chThinking', agentName(st.runAgent || st.agent)));
    else if (st.busyWith) status.append(icon('clock'), t('chBusy'));
  };

  async function run(mode, text) {
    const scan = currentScan();
    if (!scan) return;
    // Running before the request: a run that fails at once can send its "done" event before the POST answers.
    st.running = true; st.busyWith = null; st.runAgent = st.agent; controls();
    try {
      await post('/api/chat/send', { scan: scan.id, findingId: f.id, agent: st.agent, mode, text: text || null, model: model?.value || null });
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
        // A question to another agent than before: say so, its conversation starts fresh.
        if (ev.agent && st.lastAgent && ev.agent !== st.lastAgent) msgs.appendChild(h('div.chat-switch', icon('robot'), h('span', t('chSwitched', agentName(ev.agent)))));
        if (ev.agent) st.lastAgent = ev.agent;
        msgs.appendChild(h('div.msg.me', ev.subtype && ev.subtype !== 'ask' ? icon(ev.subtype === 'deep' ? 'search' : 'bolt') : null, h('span', ev.text || '')));
        st.aiText = null;
        break;
      case 'text':
        if (!st.aiText) {
          st.aiText = h('div.msg-text'); st.aiRaw = '';
          msgs.appendChild(h('div.msg.ai', { title: agentName(ev.agent || st.lastAgent || st.agent) }, icon('robot'), st.aiText));
        }
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
        if (ev.subtype && ev.subtype !== 'success') msgs.appendChild(h('div.msg-step.warn', icon('warn'), h('span', ev.subtype === 'error_max_turns' ? t('chMaxTurns') : t('anFailed', ev.text || ev.subtype))));
        break;
      case 'error':
        msgs.appendChild(h('div.msg-step.bad', icon('xCircle'), h('span', ev.text || '')));
        break;
      default: break;
    }
    if (live) msgs.scrollTop = msgs.scrollHeight;
  }

  // History of this finding's chat (kept by the server while Ferahla runs), and which agents are usable.
  msgs.appendChild(h('div.chat-hint', t('chLoading')));
  controls();
  try {
    const s = await get('/api/chat?finding=' + encodeURIComponent(f.id));
    if (open !== st) return;
    clear(msgs);
    st.agents = s.agents || [];
    if (!st.agents.some(isReady)) {
      const c = st.agents.find(a => a.installed) || st.agents[0];
      msgs.appendChild(h('div.chat-hint', icon('info'), h('p', c?.installed ? t('anNotLoggedIn', c.name) : t('chNoAgent')),
        h('button.btn.btn-sm', { onclick: () => { st.close(); go('analysis'); } }, icon('robot'), t('chSetUp'))));
      return;
    }
    // Continue with the agent of this conversation if it is still ready, else the remembered or first ready one.
    st.agent = s.agent && isReady(st.agents.find(a => a.id === s.agent)) ? s.agent : pickAgent(st.agents);
    drawPickers();
    st.ready = true;
    st.running = s.running;
    st.busyWith = s.busyWith && s.busyWith !== f.id ? s.busyWith : null;
    if (f.note) {
      const who = noteAuthor(f.noteSource);
      msgs.appendChild(h('div.note-card', verdictChip(f.noteVerdict), h('div', h('b', who.label), h('p', f.note))));
    }
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
