// New-task card: title, optional description, directory (typed), model picker and effort segments.
// The title and description are kept as a draft in localStorage until the task is created.
import { $, toast, base } from './ui.js';
import { api } from './client.js';
import { createPicker, loadCatalog } from './picker.js';

const store = {
  get: (k) => { try { return localStorage.getItem(k) || ''; } catch { return ''; } },
  set: (k, v) => { try { v ? localStorage.setItem(k, v) : localStorage.removeItem(k); } catch { /* storage unavailable */ } },
};

let onCreated;
const form = $('#newform');
const dirInput = form.elements.directory;
const picker = createPicker($('#nt-model'), $('#nt-effort'));

// ---------- form ----------
function syncCreate() {
  $('#nt-create').disabled = !form.elements.title.value.trim();
}
function checkDir() {
  const v = dirInput.value.trim();
  const bad = !!v && !(/^[a-zA-Z]:[\\/]/.test(v) || v.startsWith('\\\\') || v.startsWith('/'));
  dirInput.classList.toggle('invalid', bad);
  dirInput.title = bad ? 'Use an absolute path, e.g. D:\\repos\\velo' : base(v) ? 'Repository: ' + base(v) : '';
  return !bad;
}

export function init(created) {
  onCreated = created;
  dirInput.value = store.get('velo.dir');
  checkDir();
  syncCreate();
  loadCatalog(true);

  const draft = () => { try { return JSON.parse(store.get('velo.draft') || '{}'); } catch { return {}; } };
  const saveDraft = () => store.set('velo.draft', form.elements.title.value || form.elements.description.value
    ? JSON.stringify({ title: form.elements.title.value, description: form.elements.description.value }) : '');
  const d = draft();
  form.elements.title.value = d.title || '';
  form.elements.description.value = d.description || '';
  if (d.description) { form.elements.description.hidden = false; $('#nt-desc-btn').classList.add('on'); }
  syncCreate();
  form.elements.title.addEventListener('input', () => { syncCreate(); saveDraft(); });
  form.elements.description.addEventListener('input', saveDraft);
  dirInput.addEventListener('input', checkDir);
  $('#nt-desc-btn').addEventListener('click', () => {
    const d = form.elements.description;
    d.hidden = !d.hidden;
    $('#nt-desc-btn').classList.toggle('on', !d.hidden);
    if (!d.hidden) d.focus();
  });
  form.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); form.requestSubmit(); }
  });
  form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    if (!checkDir()) { toast('Directory must be an absolute path'); dirInput.focus(); return; }
    const body = {};
    for (const [k, v] of new FormData(form)) if (v.trim()) body[k] = v.trim();
    if (picker.model) body.model = picker.model;
    if (picker.effort) body.effort = picker.effort;
    $('#nt-create').disabled = true;
    try {
      const created = await api('POST', '/issues', body);
      if (body.directory) store.set('velo.dir', body.directory);
      form.elements.title.value = '';
      form.elements.description.value = '';
      form.elements.description.hidden = true;
      $('#nt-desc-btn').classList.remove('on');
      saveDraft();
      onCreated(created);
    } catch (e) { toast(e.message); }
    syncCreate();
  });
}

export const focusTitle = () => form.elements.title.focus();
export const fillTitle = (text) => { form.elements.title.value = text; syncCreate(); focusTitle(); };
