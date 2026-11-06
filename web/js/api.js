// Talks to the local Ferahla server. The per-launch token arrives in the URL fragment (never sent to a server),
// is kept in sessionStorage for this tab, and goes with every request in a header.

const KEY = 'ferahla-token';

function readToken() {
  // #token=<hex>[&r=<route>]: the launcher may also say which page to open.
  const m = /^#token=([0-9a-f]{64})(?:&r=([\w/?=&%.-]*))?/.exec(location.hash);
  if (m) {
    try { sessionStorage.setItem(KEY, m[1]); } catch { /* private mode: keep it in memory only */ }
    history.replaceState(null, '', location.pathname + location.search + (m[2] ? '#/' + m[2] : ''));
    return m[1];
  }
  try { return sessionStorage.getItem(KEY); } catch { return null; }
}

const token = readToken();
export const hasToken = () => !!token;

export class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

async function call(method, path, body) {
  const res = await fetch(path, {
    method,
    headers: { 'X-Ferahla-Token': token || '', ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
    cache: 'no-store',
    credentials: 'omit',
  });
  const type = res.headers.get('content-type') || '';
  const data = type.includes('json') ? await res.json() : await res.text();
  if (!res.ok) throw new ApiError(res.status, (data && data.error) || String(data) || res.statusText);
  return data;
}

export const get = path => call('GET', path);
export const post = (path, body = {}) => call('POST', path, body);

export async function download(path, fallbackName) {
  const res = await fetch(path, { headers: { 'X-Ferahla-Token': token || '' }, cache: 'no-store' });
  if (!res.ok) throw new ApiError(res.status, await res.text());
  const name = /filename="([^"]+)"/.exec(res.headers.get('content-disposition') || '')?.[1] || fallbackName;
  const url = URL.createObjectURL(await res.blob());
  const a = document.createElement('a');
  a.href = url; a.download = name; a.click();
  setTimeout(() => URL.revokeObjectURL(url), 10000);
}

/**
 * Server-sent events over fetch (EventSource can't send the token header). Reconnects with backoff.
 * onEvent(type, data); onStatus(connected: boolean).
 */
export function events(onEvent, onStatus) {
  let stopped = false, delay = 500;
  (async function loop() {
    while (!stopped) {
      try {
        const res = await fetch('/api/events', { headers: { 'X-Ferahla-Token': token || '' }, cache: 'no-store' });
        if (!res.ok || !res.body) throw new Error(String(res.status));
        onStatus(true); delay = 500;
        const reader = res.body.pipeThrough(new TextDecoderStream()).getReader();
        let buf = '';
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buf += value;
          let i;
          while ((i = buf.indexOf('\n\n')) >= 0) {
            const frame = buf.slice(0, i); buf = buf.slice(i + 2);
            let type = 'message', data = '';
            for (const line of frame.split('\n')) {
              if (line.startsWith('event: ')) type = line.slice(7);
              else if (line.startsWith('data: ')) data += line.slice(6);
            }
            if (data) { try { onEvent(type, JSON.parse(data)); } catch { /* ignore a bad frame */ } }
          }
        }
      } catch { /* fall through to reconnect */ }
      if (stopped) break;
      onStatus(false);
      await new Promise(r => setTimeout(r, delay));
      delay = Math.min(delay * 2, 8000);
    }
  })();
  return () => { stopped = true; };
}
