// Velo web UI entry: state, polling, new-task bar, filter, shortcuts, tab badge.
import { $, dur, rel, toast } from './ui.js';
import { api } from './client.js';
import { renderList, matches } from './list.js';
import * as detail from './detail.js';
import * as newtask from './newtask.js';

let issues = [];
let search = '';
let doneOpen = false;
let flashKey = null;

// ---------- tab badge ----------
const favicon = document.querySelector('link[rel="icon"]');
function renderTop() {
  const review = issues.filter((i) => i.status === 'review').length;
  const badge = $('#badge');
  badge.hidden = !review;
  badge.textContent = review + ' to review';
  document.title = review ? `(${review}) Velo` : 'Velo';
  setFaviconBadge(review);
}
function setFaviconBadge(count) {
  if (!favicon) return;
  if (!count) { favicon.href = '/favicon.svg'; return; }
  const c = document.createElement('canvas');
  c.width = c.height = 32;
  const x = c.getContext('2d');
  x.lineWidth = 3.2;
  x.lineCap = x.lineJoin = 'round';
  x.strokeStyle = matchMedia('(prefers-color-scheme: dark)').matches ? '#ededed' : '#171717';
  x.beginPath(); x.moveTo(8, 9); x.lineTo(16, 24); x.lineTo(24, 9); x.stroke();
  x.fillStyle = '#ee0000';
  x.beginPath(); x.arc(25, 25, 8.5, 0, 2 * Math.PI); x.fill();
  x.fillStyle = '#fff';
  x.font = '700 11px system-ui';
  x.textAlign = x.textBaseline = 'middle';
  x.fillText(count > 9 ? '9+' : String(count), 25, 26);
  favicon.href = c.toDataURL();
}

// ---------- desktop notification ----------
// A task that reaches review while the tab is in the background pings once; permission is asked on the first "Create".
const seen = new Map(); // key → last status
const canNotify = () => 'Notification' in window && Notification.permission === 'granted';
function notifyReviews(list) {
  for (const i of list) {
    const was = seen.get(i.key);
    seen.set(i.key, i.status);
    if (was === undefined || was === i.status || i.status !== 'review' || !document.hidden || !canNotify()) continue;
    const bad = i.runs.at(-1)?.status !== 'completed';
    const n = new Notification(`${i.key} ${bad ? 'needs a look' : 'is ready for review'}`, { body: i.title, tag: i.key });
    n.onclick = () => { window.focus(); location.hash = i.key; n.close(); };
  }
}

// ---------- list ----------
function renderAll() {
  renderTop();
  renderList($('#list'), issues, {
    search, doneOpen, flashKey, selected: detail.current(),
    onToggleDone: () => { doneOpen = !doneOpen; renderAll(); },
  });
  renderEmpty();
  detail.render(issues);
  flashKey = null;
}

function renderEmpty() {
  const e = $('#empty');
  const noTasks = !issues.length;
  const searching = !!search.trim();
  const noMatch = !noTasks && searching && !issues.some((i) => matches(i, search));
  $('#welcome').hidden = !noTasks;
  $('#caught').hidden = noTasks || searching || issues.some((i) => i.status !== 'done');
  e.hidden = !noMatch;
  e.textContent = `No results for “${search.trim()}”.`;
}

// ---------- refresh loop ----------
async function refresh() {
  const conn = $('#conn');
  try {
    issues = await api('GET', '/issues?all=true');
    notifyReviews(issues);
    conn.classList.remove('off');
    conn.title = 'online';
    $('#offline').hidden = true;
  } catch (e) {
    conn.classList.add('off');
    conn.title = 'offline: ' + e.message;
    $('#offline').hidden = false;
  }
  renderAll();
}

// ---------- new task ----------
newtask.init(async (created) => {
  if ('Notification' in window && Notification.permission === 'default') Notification.requestPermission();
  toast(created.key + ' created', 'ok');
  flashKey = created.key;
  await refresh();
});
for (const b of document.querySelectorAll('.ex')) b.addEventListener('click', () => newtask.fillTitle(b.textContent));
$('#badge').addEventListener('click', () => $('#list').scrollIntoView({ behavior: 'smooth', block: 'start' }));

// ---------- filter / routing / shortcuts ----------
const searchEl = $('#search');
function setSearch(v) {
  search = v;
  searchEl.value = v;
  if (!v) searchEl.hidden = true;
  renderAll();
}
searchEl.addEventListener('input', (e) => { search = e.target.value; renderAll(); });
searchEl.addEventListener('blur', () => { if (!searchEl.value) searchEl.hidden = true; });

function route() {
  const k = location.hash.slice(1).toUpperCase();
  if (/^VL-\d+$/.test(k)) detail.open(k);
  else if (detail.current()) detail.close();
  renderAll();
}

document.addEventListener('keydown', (e) => {
  const t = e.target;
  const typing = t instanceof HTMLElement && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable);
  if (e.key === 'Escape') {
    if (t === searchEl || (search && !detail.current())) setSearch('');
    else if (detail.current()) { detail.close(); renderAll(); }
    else if (typing) t.blur();
    return;
  }
  if (typing || e.ctrlKey || e.metaKey || e.altKey) return;
  if (e.key === '/') { e.preventDefault(); searchEl.hidden = false; searchEl.focus(); }
  else if (e.key === 'n') { e.preventDefault(); newtask.focusTitle(); }
  else if (e.key === 'j' || e.key === 'k') step(e.key === 'j' ? 1 : -1);
  else if (e.key === 'a' || e.key === 'r') { if (detail.focus(e.key === 'a' ? 'approve' : 'followup')) e.preventDefault(); }
});

// j / k open the next / previous task in the order the list shows them
function step(dir) {
  const rows = [...document.querySelectorAll('#list .row-i')];
  if (!rows.length) return;
  const at = rows.findIndex((r) => r.classList.contains('sel'));
  const next = rows[at < 0 ? (dir > 0 ? 0 : rows.length - 1) : Math.min(Math.max(at + dir, 0), rows.length - 1)];
  location.hash = next.getAttribute('href');
  next.scrollIntoView({ block: 'nearest' });
}
// Clicking the empty page behind the drawer closes it.
$('main').addEventListener('click', (e) => {
  if (detail.current() && !e.target.closest('a, button, input, textarea, label')) { detail.close(); renderAll(); }
});
window.addEventListener('hashchange', route);

detail.init($('#detail'), refresh);
setInterval(() => { if (!document.hidden || canNotify()) refresh(); }, 2000); // a hidden tab keeps polling only to notify
setInterval(() => { if (!document.hidden) detail.poll(); }, 1000);
setInterval(() => {
  for (const e of document.querySelectorAll('[data-elapsed]')) {
    const start = new Date(e.dataset.start).getTime();
    if (Number.isFinite(start)) e.textContent = dur(Date.now() - start);
  }
  for (const e of document.querySelectorAll('[data-rel]')) e.textContent = rel(e.dataset.rel);
}, 1000);
refresh().then(route);
