// Issue detail drawer: one scroll — earlier runs (folded), summary, changes (folded), log — and a footer with the two human actions.
// Polling re-renders only what changed, and each task's diff and logs are kept while it is open, so nothing blinks or jumps.
import { el, fill, put, badge, issueBadge, runDot, abs, rel, dur, tok, copy, toast } from './ui.js';
import { api } from './client.js';
import { createPicker } from './picker.js';

let host;      // the #detail element
let refresh;   // app refresh callback
let issues = [];
let cur = null;      // state of the open task (see fresh()); its DOM nodes live in cur.n
let openPanel;       // currently open ⋯ menu panel, for click-away closing
let picker;          // model / effort picker of the open drawer's footer
const states = new Map(); // key → state, so coming back to a task shows its diff and logs at once
const KEEP = 8;
const MERGE_EVERY = 15000; // ms between merge re-checks while a task is in review

const fresh = (key) => ({
  key, run: null, autoScroll: true,
  panes: new Map(),                       // run number → {pre, offset, done, buf}
  diffText: undefined, diffFiles: null, diffAt: null, diffVer: 0, merge: null, mergeAt: 0, diffOpen: new Set(), changesOpen: false,
});

export function init(container, onRefresh) {
  host = container;
  refresh = onRefresh;
  document.addEventListener('click', (e) => {
    if (openPanel && !e.target.closest('.menu')) closeMenu();
  });
}

export const current = () => cur?.key ?? null;

// Keyboard: `a` and `r` only move focus — Enter / Space then confirms, so nothing is approved by a stray key.
export function focus(what) {
  const n = cur?.n;
  const target = what === 'approve' ? n?.btnApprove : n?.msg;
  if (!target || target.hidden || target.disabled) return false;
  target.focus();
  return true;
}

export function close() {
  cur = null;
  host.hidden = true;
  document.body.classList.remove('detail-open');
  if (location.hash) history.replaceState(null, '', location.pathname);
}

export function open(key) {
  if (cur?.key === key) return;
  cur = states.get(key) ?? fresh(key);
  states.delete(key); states.set(key, cur); // most recent last
  for (const k of states.keys()) { if (states.size <= KEEP) break; states.delete(k); }
  build();
  host.hidden = false;
  document.body.classList.add('detail-open');
  render(issues);
  poll();
}

const issue = () => issues.find((x) => x.key === cur.key);
const total = (i) => i.runs.reduce((a, r) => a + (r.tokens || 0), 0);

export function render(list) {
  if (list !== undefined) {
    issues = list;
    for (const k of states.keys()) if (!issues.some((x) => x.key === k)) states.delete(k); // deleted tasks
  }
  if (!cur) return;
  const i = issue();
  if (!i) { close(); return; }
  const n = cur.n;
  const last = i.runs[i.runs.length - 1];

  n.title.textContent = i.title;
  n.title.title = i.title;
  put(n.sub, [i.status, last?.number, last?.status, last?.startedAt, last?.endedAt, total(i)].join('|'), () => [
    issueBadge(i.status),
    i.status === 'review' && last && last.status !== 'completed' ? el('span', { class: 'bad', textContent: last.status }) : null,
    last ? el('span', { class: 'mono muted', textContent: 'run ' + last.number }) : null,
    i.status === 'running' && last
      ? el('span', { class: 'mono muted', 'data-elapsed': '', 'data-start': last.startedAt, textContent: dur(Date.now() - new Date(last.startedAt)) })
      : last?.endedAt ? el('span', { class: 'mono muted', textContent: dur(new Date(last.endedAt) - new Date(last.startedAt)) }) : null,
    i.runs.length ? el('span', { class: 'mono muted', textContent: tok(total(i)) + ' tok' }) : null,
    el('span', { class: 'muted', 'data-rel': i.updatedAt, textContent: rel(i.updatedAt), title: abs(i.updatedAt) })]);

  put(n.result, [last?.number, last?.status, last?.summary].join('|'), () => resultEl(last));
  put(n.desc, i.description || '', () => (i.description ? el('p', { class: 'pre wrap-any desc', textContent: i.description }) : null));
  n.dir.textContent = i.directory || 'no directory';
  n.dir.title = i.directory || '';

  // footer: always the same two rows; only what applies to the current status is enabled
  const review = i.status === 'review';
  n.msg.disabled = !review;
  n.msg.placeholder = review ? 'Follow-up for another run…  (Enter to send)'
    : i.status === 'running' ? 'Running — follow-ups are possible once it finishes'
    : i.status === 'todo' ? 'Queued — waiting for a free slot'
    : 'Approved — nothing left to do';
  n.takeOver.hidden = !i.takeOver;
  n.btnRun.hidden = !review;
  n.btnOpts.hidden = !review;
  n.opts.hidden = !review || !n.optsOpen;
  if (!n.touched) picker.set(i.model, i.effort); // until the user picks, show what the task uses now
  n.btnApprove.hidden = !review;
  n.btnStop.hidden = !['running', 'todo'].includes(i.status);

  renderRuns(i);
  n.logSec.hidden = !last;
  if (last) renderLogCaption(i.runs.find((x) => x.number === cur.run) || last);

  const stamp = i.updatedAt + '|' + i.runs.length;
  if (cur.diffAt !== stamp) { cur.diffAt = stamp; loadDiff(i); }
  if (i.status === 'review' && i.directory && Date.now() - cur.mergeAt > MERGE_EVERY) loadMerge(i); // main may move while it waits
  else if (i.status !== 'review' && cur.merge) { cur.merge = null; cur.diffVer++; }
  renderDiff();
}

function resultEl(last) {
  if (!last) return el('p', { class: 'muted', textContent: 'Not run yet. Velo runs it in an isolated workspace and hands the result back for review.' });
  if (last.status === 'running') return null; // the header and the log already show a live run
  return el('p', { class: 'summary pre wrap-any', textContent: last.summary || '(no summary)' });
}

// ---------- earlier runs ----------
function renderRuns(i) {
  const n = cur.n;
  n.runsFold.hidden = i.runs.length < 2;
  const sel = i.runs.find((r) => r.number === cur.run) || i.runs[i.runs.length - 1];
  const sig = [sel?.number, ...i.runs.map((r) => [r.number, r.status, r.tokens, r.endedAt, r.message, r.summary].join('~'))].join('|');
  put(n.dynRuns, sig, () => {
    n.runsSum.replaceChildren('Runs', el('span', { class: 'n', textContent: String(i.runs.length) }));
    return [...i.runs].reverse().map((r) => {
      const running = r.status === 'running';
      return el('div', {
        class: 'runitem' + (r === sel ? ' sel' : ''),
        title: 'Started ' + abs(r.startedAt),
        onclick: () => { cur.run = r.number; render(issues); poll(); },
      },
        el('div', { class: 'row small' },
          runDot(r.status),
          el('b', { class: 'mono', textContent: 'run ' + r.number }),
          badge(r.status),
          el('span', { class: 'spacer' }),
          el('span', { class: 'mono muted' },
            running
              ? el('span', { 'data-elapsed': '', 'data-start': r.startedAt, textContent: dur(Date.now() - new Date(r.startedAt)) })
              : dur(new Date(r.endedAt || r.startedAt) - new Date(r.startedAt)),
            ' · ' + tok(r.tokens) + ' tok · ', el('span', { 'data-rel': r.endedAt || r.startedAt, textContent: rel(r.endedAt || r.startedAt) }))),
        r.message ? el('div', { class: 'runmsg', textContent: '↳ ' + r.message }) : null,
        el('div', { class: 'runsum pre wrap-any', textContent: r.summary || '(no summary)' }));
    });
  });
}

// ---------- log ----------
// One <pre> per run: switching runs swaps panes instead of clearing and refetching, so the log never blanks.
function paneFor(c, number) {
  let pane = c.panes.get(number);
  if (!pane) {
    pane = { pre: el('pre', { class: 'log' }), offset: 0, done: false, buf: '' };
    pane.pre.addEventListener('scroll', () => { // scrolling away from the end pauses Follow; returning to the end resumes it
      if (cur !== c || c.n.log.firstChild !== pane.pre || !pane.pre.clientHeight) return;
      const atEnd = pane.pre.scrollHeight - pane.pre.scrollTop - pane.pre.clientHeight < 24;
      if (c.autoScroll !== atEnd) { c.autoScroll = atEnd; c.n.autoBtn.classList.toggle('on', atEnd); }
    });
    c.panes.set(number, pane);
  }
  return pane;
}

function showPane(pane) {
  const box = cur.n.log;
  if (box.firstChild === pane.pre) return;
  box.replaceChildren(pane.pre);
  if (cur.autoScroll) pane.pre.scrollTop = pane.pre.scrollHeight;
}

let logBusy = false;
export async function poll() {
  if (!cur || logBusy) return;
  const c = cur;
  const i = issue();
  if (!i || !i.runs.length) return;
  const r = i.runs.find((x) => x.number === c.run) || i.runs[i.runs.length - 1];
  const pane = paneFor(c, r.number);
  showPane(pane);
  renderLogCaption(r);
  if (pane.done && r.status !== 'running') return;
  logBusy = true;
  try {
    const res = await api('GET', `/issues/${c.key}/logs?run=${r.number}&offset=${pane.offset}`);
    const pre = pane.pre;
    if (res.text) {
      pane.buf += res.text;
      const lines = pane.buf.split('\n');
      pane.buf = lines.pop() ?? ''; // the tail may be a partial line
      pre.append(...lines.map((line) => el('span', { class: line.includes('] velo: ') ? 'vl' : '', textContent: line + '\n' })));
    }
    if (res.done && pane.buf) { pre.append(pane.buf + '\n'); pane.buf = ''; }
    pane.offset = res.offset;
    pane.done = res.done;
    if (c.autoScroll) pre.scrollTop = pre.scrollHeight;
  } catch (e) { if (e.status !== 404) toast(e.message); }
  finally { logBusy = false; }
}

function renderLogCaption(r) {
  put(cur.n.logCap, r.number + '|' + r.status, () => [
    el('span', { class: 'mono muted', textContent: 'run ' + r.number }),
    badge(r.status)]);
}

// ---------- diff ----------
// The previous diff stays on screen while a new one loads; the section is only rebuilt when its data changes.
async function loadDiff(i) {
  const c = cur;
  let text = '';
  if (i.directory && i.runs.length) {
    try { text = await api('GET', `/issues/${i.key}/diff`); } catch { /* shown as no changes */ }
  }
  c.diffText = text;
  c.diffFiles = parseDiff(text);
  c.diffVer++;
  if (cur === c) renderDiff();
}

// Would approving merge cleanly? Fetched alongside the diff and re-checked now and then, since the target branch may move.
async function loadMerge(i) {
  const c = cur;
  c.mergeAt = Date.now();
  let m = null;
  try { m = await api('GET', `/issues/${i.key}/merge`); } catch { /* no verdict: show no chip */ }
  if (JSON.stringify(m) === JSON.stringify(c.merge)) return;
  c.merge = m;
  c.diffVer++;
  if (cur === c) renderDiff();
}

function mergeChip(m) {
  if (!m) return null;
  const behind = m.behind > 0 ? `${m.target} +${m.behind} · ` : '';
  if (m.conflicts?.length) return el('span', { class: 'chipstat cx', textContent: 'conflicts', title: `Approving would conflict with ${m.target} in ${m.conflicts.join(', ')}` });
  if (m.conflicts === null) return null; // git too old to tell
  return el('span', { class: 'chipstat', textContent: behind + 'clean',
    title: `Merges cleanly into ${m.target}` + (m.behind ? ` (${m.target} has ${m.behind} newer commit${m.behind === 1 ? '' : 's'})` : '') });
}

function renderDiff() {
  if (!cur?.n) return;
  const c = cur, n = c.n;
  const files = c.diffFiles ?? [];
  n.diffFold.hidden = !files.length;
  put(n.diffSum, c.diffVer, () => {
    const add = files.reduce((a, f) => a + f.add, 0), del = files.reduce((a, f) => a + f.del, 0);
    return ['Changes',
      el('span', { class: 'n', textContent: files.length + (files.length === 1 ? ' file' : ' files') }),
      add ? el('span', { class: 'chipstat ca', textContent: '+' + add }) : null,
      del ? el('span', { class: 'chipstat cd', textContent: '−' + del }) : null,
      mergeChip(c.merge)];
  });
  put(n.diffBody, c.diffVer, () => [conflictNote(c.merge), ...files.map(fileEl)]);
}

function conflictNote(m) {
  if (!m?.conflicts?.length) return null;
  return el('div', { class: 'conflict' },
    el('b', { textContent: `Conflicts with ${m.target}` }),
    el('div', { class: 'mono small', textContent: m.conflicts.join('\n') }),
    el('div', { class: 'small', textContent: `Approve will refuse until ${m.target} is merged into the task's workspace and the conflicts are resolved (git merge ${m.target}).` }));
}

function fileEl(f) {
  const open = cur.diffOpen.has(f.path);
  const head = el('div', { class: 'fhead', onclick: () => { cur.diffOpen.has(f.path) ? cur.diffOpen.delete(f.path) : cur.diffOpen.add(f.path); cur.diffVer++; renderDiff(); } },
    el('span', { class: 'chev' + (open ? ' open' : ''), textContent: '▸' }),
    el('span', { class: 'fname mono', textContent: f.path, title: f.path }),
    f.status !== 'modified' ? el('span', { class: 'fstat ' + f.status, textContent: f.status }) : null,
    el('span', { class: 'spacer' }),
    f.add ? el('span', { class: 'chipstat ca', textContent: '+' + f.add }) : null,
    f.del ? el('span', { class: 'chipstat cd', textContent: '−' + f.del }) : null);
  const body = open && f.lines.length
    ? el('pre', { class: 'diff' }, ...f.lines.map((l) => el('span', {
        class: 'ln ' + (l.startsWith('@@') ? 'hunk' : l.startsWith('[diff truncated') ? 'meta' : l[0] === '+' ? 'add' : l[0] === '-' ? 'del' : ''),
        textContent: l || ' ',
      })))
    : null;
  return el('div', { class: 'file' + (open ? ' open' : '') }, head, body);
}

// Parses `git diff` output into [{path, status, add, del, lines}] (lines start at the first @@ hunk).
function parseDiff(text) {
  if (!text.trim()) return null;
  const files = [];
  let f = null, inHunk = false;
  for (const line of text.split('\n')) {
    if (line.startsWith('diff --git ')) {
      f = { path: '', status: 'modified', add: 0, del: 0, lines: [] };
      inHunk = false;
      files.push(f);
      const m = line.match(/^diff --git a\/(.+) b\/(.+)$/);
      if (m) f.path = m[2];
    } else if (!f) {
      continue; // preamble before the first file
    } else if (!inHunk) {
      if (line.startsWith('@@')) { inHunk = true; f.lines.push(line); }
      else if (line.startsWith('new file mode')) f.status = 'added';
      else if (line.startsWith('deleted file mode')) f.status = 'deleted';
      else if (line.startsWith('rename to ')) { f.status = 'renamed'; f.path = line.slice(10); }
      else if (line.startsWith('Binary files') || line.startsWith('GIT binary patch')) f.status = 'binary';
      else if (line.startsWith('+++ b/')) f.path = line.slice(6);
    } else if (line.startsWith('@@')) {
      f.lines.push(line);
    } else if (line.startsWith('[diff truncated')) {
      f.lines.push(line);
    } else if (line.startsWith('+')) {
      f.add++; f.lines.push(line);
    } else if (line.startsWith('-')) {
      f.del++; f.lines.push(line);
    } else {
      f.lines.push(line); // context line or "\ No newline"
    }
  }
  return files;
}

// ---------- skeleton ----------
function closeMenu() {
  if (!openPanel) return;
  openPanel.hidden = true;
  openPanel = null;
}

async function act(btn, fn) {
  if (btn) btn.disabled = true;
  try { await fn(); } catch (e) { toast(e.message); }
  if (btn) btn.disabled = false;
  await refresh();
}

function build() {
  const c = cur;
  const n = c.n = {};
  n.title = el('h1');
  n.sub = el('div', { class: 'row small meta' });
  n.dir = el('div', { class: 'mono small muted dir' });

  // delete: asks for confirmation in a small popover under the button
  const confirmPanel = el('div', { class: 'menupanel card', hidden: true });
  const askDelete = () => {
    fill(confirmPanel, el('div', { class: 'menu-confirm' },
      el('span', { textContent: `Delete ${cur.key}? This drops its branch and workspace.` }),
      el('div', { class: 'row' },
        el('button', { class: 'danger', textContent: 'Yes, delete', onclick: async () => {
          const key = cur.key;
          closeMenu();
          states.delete(key);
          close(); // leave at once; the server may need a moment to stop a running task
          try { await api('DELETE', `/issues/${key}`); toast(key + ' deleted', 'ok'); } catch (e) { toast(e.message); }
          await refresh();
        } }),
        el('button', { textContent: 'Cancel', onclick: closeMenu }))));
  };
  const del = el('div', { class: 'menu' },
    el('button', { class: 'sm danger', textContent: 'Delete', title: 'Delete this task', onclick: (e) => {
      e.stopPropagation();
      const show = confirmPanel.hidden;
      closeMenu();
      if (show) { askDelete(); confirmPanel.hidden = false; openPanel = confirmPanel; }
    } }),
    confirmPanel);
  n.takeOver = el('button', { class: 'sm', textContent: 'Take over', title: 'Copy the command that resumes this session in the agent', onclick: () => {
    const i = issue();
    if (i?.takeOver) copy(i.takeOver, 'Take-over command copied');
  } });

  const head = el('div', { class: 'dhead' },
    el('div', { class: 'row' },
      el('b', { class: 'mono small muted', textContent: cur.key }),
      el('span', { class: 'spacer' }),
      n.takeOver,
      el('button', { class: 'sm', textContent: 'Link', title: 'Copy a link to this task', onclick: () => copy(location.origin + location.pathname + '#' + cur.key, 'Link copied') }),
      del,
      el('button', { class: 'sm', textContent: '✕', title: 'Close (Esc)', 'aria-label': 'Close', onclick: () => { close(); refresh(); } })),
    n.title, n.sub, n.dir);

  n.result = el('div');
  n.desc = el('div');

  // changes: folded, the summary line carries the totals
  n.diffSum = el('summary', { textContent: 'Changes' });
  n.diffBody = el('div', { class: 'files' });
  n.diffFold = el('details', { class: 'fold changes', open: cur.changesOpen, hidden: true, ontoggle: () => { cur.changesOpen = n.diffFold.open; } }, n.diffSum, n.diffBody);

  // log: always shown at a fixed height
  n.logCap = el('span', { class: 'row small' });
  n.autoBtn = el('button', { class: 'sm' + (cur.autoScroll ? ' on' : ''), textContent: 'Follow', title: 'Auto-scroll to the newest output', onclick: () => {
    cur.autoScroll = !cur.autoScroll;
    n.autoBtn.classList.toggle('on', cur.autoScroll);
    const pre = n.log.firstChild;
    if (cur.autoScroll && pre) pre.scrollTop = pre.scrollHeight;
  } });
  n.log = el('div', { class: 'logbox' });
  n.logSec = el('section', { class: 'logsec' },
    el('div', { class: 'row logbar' }, el('b', { class: 'lbl', textContent: 'Log' }), n.logCap, el('span', { class: 'spacer' }), n.autoBtn,
      el('button', { class: 'sm', textContent: 'Copy', onclick: () => copy(n.log.firstChild?.textContent ?? '', 'Log copied') })),
    n.log);

  // earlier runs: folded, on top
  n.runsSum = el('summary', { textContent: 'Runs' });
  n.dynRuns = el('div', { class: 'runs' });
  n.runsFold = el('details', { class: 'fold' }, n.runsSum, n.dynRuns);

  // footer: follow-up message (Enter = run again) + Approve / Stop; always present so the drawer keeps its shape
  n.msg = el('textarea', { rows: 1, placeholder: 'Follow-up for another run…  (Enter to send)', onkeydown: (e) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing && n.msg.value.trim()) { e.preventDefault(); n.btnRun.click(); }
  } });
  // model / effort for the next run: folded away, opened from the footer; only sent when the user changed something
  picker?.destroy();
  n.optsOpen = false; n.touched = false;
  n.opts = el('div', { class: 'nt-row opts', hidden: true });
  const modelBox = el('div', { class: 'nt-model' }), effortBox = el('div', { class: 'seg' });
  n.opts.append(modelBox, effortBox);
  picker = createPicker(modelBox, effortBox, { up: true, onChange: () => { n.touched = true; n.btnOpts.classList.add('on'); } });
  n.btnOpts = el('button', { class: 'sm ghost', textContent: 'Model', title: 'Choose the model and effort for the next run', onclick: () => {
    n.optsOpen = !n.optsOpen;
    n.opts.hidden = !n.optsOpen;
  } });
  n.btnRun = el('button', { textContent: 'Run again', onclick: () => act(n.btnRun, async () => {
    const m = n.msg.value.trim();
    const body = {};
    if (m) body.message = m;
    if (n.touched) { body.model = picker.model; body.effort = picker.effort; }
    await api('POST', `/issues/${c.key}/run`, Object.keys(body).length ? body : undefined);
    n.msg.value = '';
    n.touched = false; n.optsOpen = false; n.btnOpts.classList.remove('on');
    c.run = null; // follow the new run
  }) });
  n.btnStop = el('button', { textContent: 'Stop', onclick: () => act(n.btnStop, () => api('POST', `/issues/${cur.key}/stop`)) });
  n.btnApprove = el('button', { class: 'primary', textContent: 'Approve', onclick: () => act(n.btnApprove, () => api('POST', `/issues/${cur.key}/approve`)) });
  n.foot = el('div', { class: 'dfoot' }, n.msg, n.opts, el('div', { class: 'row btns' }, n.btnRun, n.btnOpts, el('span', { class: 'spacer' }), n.btnStop, n.btnApprove));

  host.replaceChildren(head, el('div', { class: 'dbody' }, n.runsFold, n.result, n.desc, n.diffFold, n.logSec), n.foot);
}
