// Line icons on a 24px grid (stroke 1.8), drawn for Ferahla. Built with createElementNS: no innerHTML.

const P = {
  overview: 'M4 13a8 8 0 1 1 16 0M12 13l4-4M4 17h16',
  sparkle: 'M12 3l1.9 4.6L18.5 9.5l-4.6 1.9L12 16l-1.9-4.6L5.5 9.5l4.6-1.9L12 3zM19 15l.8 1.9 1.9.8-1.9.8L19 20.4l-.8-1.9-1.9-.8 1.9-.8L19 15z',
  folder: 'M3 7.5A1.5 1.5 0 0 1 4.5 6h4.2l2 2h8.8A1.5 1.5 0 0 1 21 9.5v8a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17.5v-10z',
  file: 'M7 3h7l4 4v13a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1zM14 3v4h4',
  box: 'M3.5 8l8.5-4.5L20.5 8v8L12 20.5 3.5 16V8zM3.5 8L12 12.5 20.5 8M12 12.5v8',
  history: 'M4 12a8 8 0 1 0 2.3-5.7M4 4v3.5h3.5M12 8v4.5l3 2',
  drive: 'M4 14.5l2.2-8.1A2 2 0 0 1 8.1 5h7.8a2 2 0 0 1 1.9 1.4l2.2 8.1M4 14.5h16v3a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 17.5v-3zM16.5 16.8h.01',
  scan: 'M4 8V5a1 1 0 0 1 1-1h3M16 4h3a1 1 0 0 1 1 1v3M20 16v3a1 1 0 0 1-1 1h-3M8 20H5a1 1 0 0 1-1-1v-3M4 12h16',
  chevron: 'M9 6l6 6-6 6',
  chevDown: 'M6 9l6 6 6-6',
  up: 'M12 19V5M6 11l6-6 6 6',
  search: 'M10.5 17a6.5 6.5 0 1 0 0-13 6.5 6.5 0 0 0 0 13zM15.5 15.5L20 20',
  check: 'M5 12.5l4.5 4.5L19 7.5',
  checkCircle: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM8 12.5l2.7 2.7L16 9.8',
  warn: 'M12 4l9 16H3l9-16zM12 10v4.5M12 17.5h.01',
  info: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 11v5.5M12 7.8h.01',
  x: 'M6 6l12 12M18 6L6 18',
  xCircle: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM9 9l6 6M15 9l-6 6',
  skip: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM8 12h8',
  undo: 'M9 14L4 9l5-5M4 9h10.5a5.5 5.5 0 0 1 0 11H11',
  trash: 'M4 7h16M9.5 7V4.5h5V7M6.5 7l.9 12.2A1.5 1.5 0 0 0 8.9 20.6h6.2a1.5 1.5 0 0 0 1.5-1.4L17.5 7M10 11v6M14 11v6',
  sun: 'M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM12 2.5v2M12 19.5v2M4.6 4.6l1.4 1.4M18 18l1.4 1.4M2.5 12h2M19.5 12h2M4.6 19.4L6 18M18 6l1.4-1.4',
  moon: 'M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z',
  monitor: 'M3.5 5h17v11h-17zM9 20h6M12 16v4',
  external: 'M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5',
  copy: 'M9 9h10v11H9zM5 15H4V4h11v1',
  shield: 'M12 3l8 3v6c0 4.5-3.4 8.3-8 9-4.6-.7-8-4.5-8-9V6l8-3z',
  lock: 'M6 11h12v9H6zM8.5 11V8a3.5 3.5 0 0 1 7 0v3',
  power: 'M12 3v8M6.4 6.4a8 8 0 1 0 11.2 0',
  download: 'M12 4v11M7 10.5l5 5 5-5M4.5 19.5h15',
  trend: 'M3.5 16.5l5.5-5.5 4 4 7.5-7.5M15 7.5h5.5V13',
  layers: 'M12 4l8.5 4.5L12 13 3.5 8.5 12 4zM3.5 12.5L12 17l8.5-4.5M3.5 16.5L12 21l8.5-4.5',
  bolt: 'M13 3L5 13.5h6L10 21l8-10.5h-6L13 3z',
  game: 'M7 9h10a4 4 0 0 1 4 4v1.5a2.5 2.5 0 0 1-4.6 1.4L15 14H9l-1.4 1.9A2.5 2.5 0 0 1 3 14.5V13a4 4 0 0 1 4-4zM8 11v3M6.5 12.5h3M15.5 12h.01M17.5 13.5h.01',
  copyDup: 'M8 8h11v12H8zM5 16V4h11',
  archive: 'M4 5h16v4H4zM5.5 9v10h13V9M10 13h4',
  installer: 'M12 3v10M8 9l4 4 4-4M5 15v4h14v-4',
  wrench: 'M14.5 6.5a4 4 0 0 0 5 5L12 19a2.1 2.1 0 0 1-3-3l7.5-7.5zM6 6l3 3M4.5 4.5L6 6',
  code: 'M8.5 8L4.5 12l4 4M15.5 8l4 4-4 4M13.5 5l-3 14',
  cpu: 'M7 7h10v10H7zM10 10h4v4h-4zM10 3v4M14 3v4M10 17v4M14 17v4M3 10h4M3 14h4M17 10h4M17 14h4',
  clock: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 7v5l3.5 2',
  robot: 'M7 9h10a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2v-6a2 2 0 0 1 2-2zM12 5v4M9.5 14h.01M14.5 14h.01M3 13v2M21 13v2',
  play: 'M8 5.5v13l10-6.5-10-6.5z',
  stop: 'M7 7h10v10H7z',
  apps: 'M4.5 4.5h6v6h-6zM13.5 4.5h6v6h-6zM4.5 13.5h6v6h-6zM13.5 13.5h6v6h-6z',
  terminal: 'M4 5h16v14H4zM7.5 9.5l3 2.5-3 2.5M12.5 15h4',
  note: 'M6 4h12v16H6zM9 8h6M9 12h6M9 16h3',
  send: 'M20.5 3.5L3.5 10.6l6.8 2.9 2.9 6.8 7.3-16.8zM10.3 13.5l4.6-4.6',
  dots: 'M6 12h.01M12 12h.01M18 12h.01',
};

const CAT = {
  cache: 'layers', build: 'code', installer: 'installer', archive: 'archive', 'old-version': 'history',
  duplicate: 'copyDup', partial: 'download', 'agent-temp': 'robot', game: 'game', system: 'cpu', repo: 'code',
};

export function icon(name, cls = '') {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '1.8');
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('class', ('ico ' + cls).trim());
  const path = document.createElementNS(ns, 'path');
  path.setAttribute('d', P[name] || P.info);
  svg.appendChild(path);
  return svg;
}

export const catIcon = c => icon(CAT[c] || 'sparkle');

/** The Ferahla mark: a drive ring opening into a breeze. */
export function brandMark(cls = 'brand-mark') {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 0 48 48');
  svg.setAttribute('class', cls);
  svg.setAttribute('aria-hidden', 'true');
  const add = (tag, attrs) => { const e = document.createElementNS(ns, tag); for (const k in attrs) e.setAttribute(k, attrs[k]); svg.appendChild(e); return e; };
  const g = add('linearGradient', { id: 'fg', x1: '0', y1: '0', x2: '1', y2: '1' });
  const s1 = document.createElementNS(ns, 'stop'); s1.setAttribute('offset', '0'); s1.setAttribute('stop-color', '#1b8aa6');
  const s2 = document.createElementNS(ns, 'stop'); s2.setAttribute('offset', '1'); s2.setAttribute('stop-color', '#2fb38a');
  g.append(s1, s2);
  const defs = document.createElementNS(ns, 'defs'); defs.appendChild(g); svg.insertBefore(defs, svg.firstChild);
  add('rect', { x: '2', y: '2', width: '44', height: '44', rx: '13', fill: 'url(#fg)' });
  add('path', { d: 'M24 12.5a11.5 11.5 0 1 0 11.2 14.1', fill: 'none', stroke: '#fff', 'stroke-width': '3.4', 'stroke-linecap': 'round' });
  add('path', { d: 'M27 18.5h10.5M29.5 24h8M27 29.5h6.5', fill: 'none', stroke: '#fff', 'stroke-width': '3', 'stroke-linecap': 'round', opacity: '.92' });
  add('circle', { cx: '24', cy: '24', r: '3.2', fill: '#fff' });
  return svg;
}
