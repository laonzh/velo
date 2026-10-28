// Task list: one grouped list — Needs review first, then Running, Queued, Done (collapsed).
import { el, dur, rel, abs, base } from './ui.js';

const GROUPS = [
  ['review', 'Needs review'],
  ['running', 'Running'],
  ['todo', 'Queued'],
  ['done', 'Done'],
];
const DONE_LIMIT = 20;

export function matches(i, search) {
  const q = search.trim().toLowerCase();
  return !q || (i.title + ' ' + i.description + ' ' + i.key).toLowerCase().includes(q);
}

const lastRun = (i) => i.runs[i.runs.length - 1];

function dotClass(i) {
  if (i.status === 'running') return 'info';
  if (i.status === 'done') return 'ok';
  if (i.status === 'review') return lastRun(i)?.status === 'completed' ? 'warn' : 'bad';
  return '';
}

// Right-hand column: the one fact that matters for the row's state.
function info(i) {
  const last = lastRun(i);
  if (i.status === 'running' && last)
    return el('span', { class: 'mono', 'data-elapsed': '', 'data-start': last.startedAt, textContent: dur(Date.now() - new Date(last.startedAt)) });
  if (i.status === 'review' && last && last.status !== 'completed')
    return el('span', { class: 'bad', textContent: last.status });
  return null;
}

function row(i, opts) {
  const tip = [i.directory ? base(i.directory) : 'no directory', i.model, i.effort].filter(Boolean).join(' · ');
  return el('a', { class: 'row-i' + (i.key === opts.selected ? ' sel' : '') + (i.key === opts.flashKey ? ' flash' : ''), href: '#' + i.key, title: tip },
    el('span', { class: 'dot ' + dotClass(i) }),
    el('span', { class: 'key', textContent: i.key }),
    el('span', { class: 'ttl', textContent: i.title }),
    info(i),
    el('span', { class: 'time', 'data-rel': i.updatedAt, textContent: rel(i.updatedAt), title: abs(i.updatedAt) }));
}

export function renderList(container, issues, opts) {
  const { search, doneOpen, onToggleDone } = opts;
  // nothing visible changed since the last poll: keep the DOM (hover, focus and scroll stay put)
  const sig = JSON.stringify([search, doneOpen, opts.selected, opts.flashKey,
    issues.map((i) => [i.key, i.title, i.status, i.updatedAt, i.directory, i.model, i.effort, lastRun(i)?.status])]);
  if (container._sig === sig) return;
  container._sig = sig;
  const searching = !!search.trim();
  const sections = GROUPS.map(([s, label]) => {
    let list = issues.filter((i) => i.status === s && matches(i, search));
    if (!list.length) return null;
    list.sort(s === 'todo' || s === 'running' ? (a, b) => a.number - b.number : (a, b) => (a.updatedAt < b.updatedAt ? 1 : -1));
    const total = list.length;
    const open = s !== 'done' || doneOpen || searching;
    const head = s === 'done'
      ? el('button', { class: 'ghead fold', onclick: onToggleDone }, el('span', { class: 'chev' + (open ? ' open' : ''), textContent: '▸' }), label, el('span', { class: 'n', textContent: String(total) }))
      : el('div', { class: 'ghead' }, label, el('span', { class: 'n', textContent: String(total) }));
    if (s === 'done' && open && !searching) list = list.slice(0, DONE_LIMIT);
    return el('section', { class: 'group' }, head, open ? el('div', { class: 'rows' }, ...list.map((i) => row(i, opts))) : null);
  }).filter(Boolean);
  container.replaceChildren(...sections);
}
