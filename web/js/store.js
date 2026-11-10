// App state shared by the views: server state, the selected scan, cached data, running job.
import { get, post, events } from './api.js';

const listeners = new Set();
const jobWaiters = new Map();
const cache = new Map();

export const store = {
  state: null,       // StateDto from /api/state
  root: null,        // selected drive/folder root
  connected: true,
  selection: new Set(), // finding ids selected on the Suggestions page (kept while navigating)
};

const pref = {
  get(k, d) { try { return localStorage.getItem('ferahla-' + k) ?? d; } catch { return d; } },
  set(k, v) { try { localStorage.setItem('ferahla-' + k, v); } catch { /* storage may be blocked */ } },
};
export { pref };

export function subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); }
function emit(what, data) { for (const fn of [...listeners]) fn(what, data); }

/** Scans of the selected root, newest first. */
export function scansOf(root) {
  return (store.state?.scans || []).filter(s => s.root.toLowerCase() === (root || '').toLowerCase());
}

export function currentScan() { return scansOf(store.root)[0] || null; }

function pickRoot() {
  const s = store.state;
  if (!s) return;
  const wanted = pref.get('root', null);
  const roots = new Set(s.scans.map(x => x.root.toLowerCase()));
  if (store.root && (roots.has(store.root.toLowerCase()) || s.drives.some(d => d.root === store.root))) return;
  if (wanted && (roots.has(wanted.toLowerCase()) || s.drives.some(d => d.root === wanted))) store.root = wanted;
  else store.root = s.scans[0]?.root || s.defaultRoot || s.drives[0]?.root || 'C:\\';
}

export function selectRoot(root) {
  store.root = root;
  pref.set('root', root);
  store.selection.clear();
  emit('root');
}

export async function refreshState() {
  store.state = await get('/api/state');
  pickRoot();
  emit('state');
  return store.state;
}

/** Cached GET; cleared when the server says data changed. */
export function cached(path) {
  if (!cache.has(path)) {
    const p = get(path).catch(e => { cache.delete(path); throw e; });
    cache.set(path, p);
  }
  return cache.get(path);
}
export function dropCache() { cache.clear(); }

export function startEvents() {
  return events((type, data) => {
    if (type === 'hello') { store.state = data; pickRoot(); emit('state'); return; }
    if (type === 'job') {
      if (store.state) store.state.job = data;
      emit('job');
      if (data.finishedAt) {
        const w = jobWaiters.get(data.id);
        if (w) { jobWaiters.delete(data.id); w(data); }
      }
      return;
    }
    if (type === 'analysis') { emit('analysis', data); return; }
    if (type === 'chat') { emit('chat', data); return; }
    if (type === 'state') { dropCache(); refreshState().catch(() => {}); }
  }, ok => {
    if (ok === store.connected) return;
    store.connected = ok;
    emit(ok ? 'online' : 'offline');
    if (ok) { dropCache(); refreshState().catch(() => {}); }
  });
}

/** Resolves with the finished job. Polls as a fallback in case an event is missed. */
export function waitJob(job) {
  if (job.finishedAt) return Promise.resolve(job);
  return new Promise(resolve => {
    jobWaiters.set(job.id, resolve);
    const poll = setInterval(async () => {
      if (!jobWaiters.has(job.id)) { clearInterval(poll); return; }
      try {
        const s = await get('/api/state');
        if (s.job?.id === job.id && s.job.finishedAt) { jobWaiters.delete(job.id); clearInterval(poll); resolve(s.job); }
      } catch { /* keep waiting */ }
    }, 1500);
  });
}

export async function runJob(path, body) {
  const job = await post(path, body);
  if (store.state) store.state.job = job;
  emit('job');
  return waitJob(job);
}

export const jobRunning = () => store.state?.job?.state === 'running';
