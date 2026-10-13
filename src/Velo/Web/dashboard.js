const token = document.querySelector('meta[name="velo-token"]').content;
const message = document.getElementById('message');
const counts = document.getElementById('counts');
const template = document.getElementById('row-template');
const addForm = document.getElementById('add');
const labels = { running: '运行中', pending: '等待中', failed: '失败', succeeded: '已完成' };
const sections = Object.fromEntries(
  Object.keys(labels).map((status) => [status, document.querySelector(`#${status} .list`)]));
const rows = new Map();
let messageTimer;

async function call(method, path, body) {
  const headers = { 'X-Velo-Token': token };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  const json = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(json?.error?.message ?? `请求失败（HTTP ${response.status}）`);
  return json;
}

function notify(text, isError = false) {
  message.textContent = text;
  message.className = isError ? 'error' : '';
  clearTimeout(messageTimer);
  messageTimer = setTimeout(() => { message.textContent = ''; }, 5000);
}

function notifyError(error) {
  notify(error.message, true);
}

// Runs one user action, then reloads the list so the page shows the result.
async function run(task, success) {
  try {
    await task();
    if (success) notify(success);
  } catch (error) {
    notifyError(error);
  }
  await load().catch(notifyError);
}

function createRow(id) {
  const el = template.content.firstElementChild.cloneNode(true);
  const row = {
    id,
    el,
    editor: el.querySelector('.editor'),
    log: el.querySelector('.log'),
    logOpen: false,
    offset: 0,
    diff: el.querySelector('.diff'),
    diffOpen: false,
  };
  el.addEventListener('click', (event) => {
    const action = event.target.dataset.action;
    if (action) handle(row, action);
  });
  row.editor.addEventListener('submit', (event) => {
    event.preventDefault();
    const prompt = row.editor.querySelector('textarea').value;
    run(() => call('PATCH', `/api/v1/${id}`, { prompt }), '已保存');
  });
  return row;
}

function handle(row, action) {
  const id = row.id;
  switch (action) {
    case 'edit':
      run(async () => {
        const detail = await call('GET', `/api/v1/${id}`);
        row.editor.querySelector('textarea').value = detail.prompt;
        row.editor.hidden = false;
        row.editor.querySelector('textarea').focus();
      });
      return;
    case 'close-editor':
      row.editor.hidden = true;
      return;
    case 'cancel':
      run(async () => {
        const result = await call('POST', `/api/v1/${id}/cancel`);
        notify(result.result === 'cancelled' ? '已取消' : '已请求取消，运行中的 Codex 将被停止');
      });
      return;
    case 'retry':
      run(() => call('POST', `/api/v1/${id}/retry`), '已重新排队');
      return;
    case 'delete':
      if (!confirm('删除这条任务及其日志？')) return;
      run(() => call('DELETE', `/api/v1/${id}`), '已删除');
      return;
    case 'diff':
      row.diffOpen = !row.diffOpen;
      row.diff.hidden = !row.diffOpen;
      if (row.diffOpen) showDiff(row).catch(notifyError);
      return;
    case 'accept':
      if (!confirm('接受这次任务的改动？它会以未提交的改动应用到源仓库，worktree 和分支 velo/' + id + ' 会保留到你丢弃它为止。')) return;
      run(() => call('POST', `/api/v1/${id}/accept`), '已接受');
      return;
    case 'discard':
      if (!confirm('丢弃这次任务的改动？它的 worktree 和分支 velo/' + id + ' 会被删除，且无法恢复。')) return;
      run(() => call('POST', `/api/v1/${id}/discard`), '已丢弃');
      return;
    case 'log':
      row.logOpen = !row.logOpen;
      row.log.hidden = !row.logOpen;
      row.log.textContent = '';
      row.offset = 0;
      if (row.logOpen) pullLog(row).catch(notifyError);
      return;
  }
}

// The worktree of succeeded work does not change, so the diff is fetched once per open.
async function showDiff(row) {
  row.diff.textContent = '';
  const diff = await call('GET', `/api/v1/${row.id}/diff`);
  if (!row.diffOpen) return;
  if (diff.patch.length === 0) {
    row.diff.textContent = '没有改动。';
    return;
  }
  row.diff.textContent = `${diff.stat}\n${diff.patch}` + (diff.truncated ? '\n… 差异过长，已截断。' : '');
}

// Reads whole lines from the saved offset until the server has nothing more.
async function pullLog(row) {
  for (;;) {
    const chunk = await call('GET', `/api/v1/${row.id}/log?offset=${row.offset}`);
    row.log.textContent += chunk.text;
    if (chunk.next_offset === row.offset) break;
    row.offset = chunk.next_offset;
  }
  row.log.scrollTop = row.log.scrollHeight;
}

function badgeText(item) {
  if (item.discarded) return '已丢弃';
  if (item.accepted) return '已接受';
  if (item.cancel_requested && item.status === 'running') return '取消中';
  return labels[item.status];
}

function paint(row, item) {
  const { el } = row;
  el.dataset.status = item.status;
  el.dataset.discarded = item.discarded;
  el.dataset.accepted = item.accepted;
  el.querySelector('.badge').textContent = badgeText(item);
  el.querySelector('.title').textContent = item.title;
  el.querySelector('.meta').textContent =
    `${item.work_id} · ${item.workspace.path} · 第 ${item.attempts} 次`;

  const error = el.querySelector('.error');
  error.textContent = item.last_error ?? '';
  error.hidden = !item.last_error;

  el.querySelector('[data-action="edit"]').disabled = item.status !== 'pending';
  el.querySelector('[data-action="cancel"]').disabled =
    !['pending', 'running'].includes(item.status) || item.cancel_requested;
  el.querySelector('[data-action="retry"]').disabled = item.status !== 'failed';
  el.querySelector('[data-action="diff"]').disabled = !item.reviewable;
  el.querySelector('[data-action="accept"]').disabled = !item.reviewable || item.accepted;
  el.querySelector('[data-action="discard"]').disabled = !item.reviewable;
  el.querySelector('[data-action="delete"]').disabled = item.status === 'running';

  // Once the work leaves pending, an open editor can no longer save; close it.
  if (item.status !== 'pending') row.editor.hidden = true;
  // A diff only exists while the worktree does; close it once that is gone.
  if (!item.reviewable) {
    row.diffOpen = false;
    row.diff.hidden = true;
  }
}

async function load() {
  const { work } = await call('GET', '/api/v1/work');
  const seen = new Set();
  const total = { running: 0, pending: 0, failed: 0, succeeded: 0 };
  const placed = Object.fromEntries(Object.keys(labels).map((status) => [status, []]));

  for (const item of work) {
    let row = rows.get(item.work_id);
    if (!row) {
      row = createRow(item.work_id);
      rows.set(item.work_id, row);
    }
    seen.add(item.work_id);
    total[item.status] += 1;
    paint(row, item);
    placed[item.status].push(row);
    if (row.logOpen) pullLog(row).catch(notifyError);
  }

  for (const [id, row] of rows) {
    if (seen.has(id)) continue;
    row.el.remove();
    rows.delete(id);
  }

  // Move a row only when it is out of place, so focus and typed text survive the poll.
  for (const [status, list] of Object.entries(placed)) {
    const container = sections[status];
    list.forEach((row, index) => {
      if (container.children[index] !== row.el) {
        container.insertBefore(row.el, container.children[index] ?? null);
      }
    });
    while (container.children.length > list.length) container.lastElementChild.remove();
    container.parentElement.hidden = list.length === 0;
  }

  counts.textContent =
    `等待 ${total.pending} · 运行 ${total.running} · 失败 ${total.failed} · 完成 ${total.succeeded}`;
}

addForm.addEventListener('submit', (event) => {
  event.preventDefault();
  run(async () => {
    await call('POST', '/api/v1/work', {
      prompt: document.getElementById('add-prompt').value,
      workspace: document.getElementById('add-workspace').value,
      worktree: document.getElementById('add-worktree').checked,
    });
    document.getElementById('add-prompt').value = '';
  }, '已添加');
});

document.getElementById('refresh').addEventListener('click', () => {
  run(() => call('POST', '/api/v1/refresh'), '已请求刷新');
});

load().catch(notifyError);
setInterval(() => load().catch(notifyError), 2000);
