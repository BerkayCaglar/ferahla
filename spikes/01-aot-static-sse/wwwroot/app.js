const log = document.getElementById('log');
const t0 = performance.now();
const out = s => { log.textContent += `${Math.round(performance.now() - t0)}ms ${s}\n`; };

fetch('i18n/tr.json').then(r => r.json()).then(j => out('i18n: ' + j.hello));

const es = new EventSource('sse/typed');
es.addEventListener('tick', e => out('typed ' + e.data));
es.onerror = () => { out('typed closed'); es.close(); };
