// Shared DOM helpers and formatters. All text goes through textContent.
export const $ = (s) => document.querySelector(s);

export function el(tag, props = {}, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === 'class') e.className = v;
    else if (k === 'dataset') Object.assign(e.dataset, v);
    else if (k.startsWith('data-') || k.startsWith('aria-')) e.setAttribute(k, v);
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else e[k] = v;
  }
  e.append(...kids.filter((x) => x != null && x !== false));
  return e;
}

// Replaces a node's children, skipping null/false (replaceChildren alone would render them as the text "null").
export const fill = (node, ...kids) => node.replaceChildren(...kids.flat().filter((x) => x != null && x !== false));

// Re-renders only when `sig` changed, so polling never rebuilds (and resets) unchanged DOM.
export function put(node, sig, build) {
  if (node._sig === sig) return;
  node._sig = sig;
  fill(node, build());
}

const RUN_CLASS = { completed: 'ok', failed: 'bad', stopped: 'warn', interrupted: 'warn', running: 'info' };
const ISSUE_CLASS = { running: 'info', review: 'warn' };
const cap = (s) => s.charAt(0).toUpperCase() + s.slice(1);
export const runDot = (s) => el('span', { class: 'dot ' + (RUN_CLASS[s] || '') });
export const badge = (s) => el('span', { class: 'st' }, runDot(s), cap(s));
export const issueBadge = (s) => el('span', { class: 'st' }, el('span', { class: 'dot ' + (ISSUE_CLASS[s] || '') }), cap(s));

// number → "12.4k"
export function tok(n) {
  if (!n) return '0';
  return n >= 10000 ? (n / 1000).toFixed(1).replace(/\.0$/, '') + 'k' : String(n);
}

export function dur(ms) {
  if (ms == null || ms < 0) ms = 0;
  const s = Math.round(ms / 1000);
  if (s < 60) return s + 's';
  const m = Math.floor(s / 60), r = s % 60;
  if (m < 60) return r ? `${m}m ${r}s` : m + 'm';
  return `${Math.floor(m / 60)}h ${m % 60}m`;
}

export function rel(t) {
  if (!t) return '';
  const s = (Date.now() - new Date(t)) / 1000;
  if (s < 10) return 'just now';
  if (s < 60) return Math.floor(s) + 's ago';
  if (s < 3600) return Math.floor(s / 60) + 'm ago';
  if (s < 86400) return Math.floor(s / 3600) + 'h ago';
  if (s < 7 * 86400) return Math.floor(s / 86400) + 'd ago';
  return new Date(t).toLocaleDateString();
}

export const abs = (t) => (t ? new Date(t).toLocaleString() : '-');
export const sdate = (t) => (t ? new Date(t).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '-');
export const base = (p) => (p ? p.replace(/[\\/]+$/, '').split(/[\\/]/).pop() : '');

export function copy(text, msg = 'Copied') {
  navigator.clipboard?.writeText(text).then(() => toast(msg, 'ok')).catch(() => toast('copy failed'));
}

let toastTimer;
export function toast(msg, kind = 'error') {
  const t = $('#toast');
  t.textContent = msg; t.hidden = false;
  t.classList.toggle('ok', kind === 'ok');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.hidden = true), Math.min(4000 + msg.length * 40, 15000)); // long messages need longer to read
}
