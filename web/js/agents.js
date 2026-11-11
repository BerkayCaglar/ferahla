// The AI agents Ferahla can use (Claude Code, Codex): picking one, its models, and who wrote a note.
import { h } from './ui.js';
import { icon } from './icons.js';
import { t } from './i18n.js';
import { pref } from './store.js';

export const agentName = id => id === 'codex' ? 'Codex' : 'Claude';
export const isReady = a => !!(a && a.installed && a.loggedIn);

/** The agent to use: the remembered one if it is ready, else the first ready one, else the remembered one. */
export function pickAgent(agents, wanted = pref.get('agent', '')) {
  const by = id => (agents || []).find(a => a.id === id);
  if (isReady(by(wanted))) return wanted;
  return ((agents || []).find(isReady) || by(wanted) || (agents || [])[0])?.id || 'claude';
}

/** Segmented control: one button per agent, with a dot that says whether it is ready. */
export function agentSeg(agents, current, onPick) {
  return h('div.seg.agent-seg', { role: 'group', 'aria-label': t('anAgent') },
    agents.map(a => h('button', {
      class: a.id === current ? 'on' : '', title: a.name + ' · ' + (isReady(a) ? t('anAgentReady') : t('anAgentNotReady')),
      'aria-pressed': a.id === current ? 'true' : 'false',
      onclick: () => { if (a.id !== current) { pref.set('agent', a.id); onPick(a.id); } },
    }, h('span.agent-dot' + (isReady(a) ? '.ok' : '')), a.name)));
}

/** Model picker for one agent; the choice is remembered per agent. */
export function modelSelect(info, cls = '') {
  const key = 'model-' + info.id;
  const saved = pref.get(key, info.id === 'claude' ? pref.get('model', '') : '');
  const models = info.models || [];
  const sel = h('select.select' + (cls ? '.' + cls : ''), { 'aria-label': t('anModel') },
    [['', t('anModelDefault', info.name)], ...models.map(m => [m.id, m.label + (m.hint === 'lightest' ? ' · ' + t('anCheapest') : '')])]
      .map(([v, l]) => h('option', { value: v, text: l, selected: saved === v })));
  if (!models.some(m => m.id === saved)) sel.value = '';
  sel.addEventListener('change', () => pref.set(key, sel.value));
  return sel;
}

/** Who wrote a note: icon and label ("Claude's assessment", "Codex's assessment", "Your note"). */
export function noteAuthor(source) {
  if (source === 'claude' || source === 'codex') return { icon: 'robot', label: t('claudeSays', agentName(source)) };
  return { icon: 'note', label: t('yourNote') };
}

export const agentIcon = () => icon('robot');
