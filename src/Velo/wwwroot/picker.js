// Model picker (popover with filter) and effort segments, shared by the new-task card and the drawer's "Run again".
// Models and efforts come from GET /api/models (what the agent itself offers); without a catalog the picker is a
// plain text field and effort falls back to low / medium / high.
import { el } from './ui.js';
import { api } from './client.js';

// Effort slots are always the same set, so the layout never reflows: a model that lacks one just dims it.
const EFFORT_ORDER = ['minimal', 'low', 'medium', 'high', 'xhigh', 'max', 'ultra'];
const rank = (e) => (EFFORT_ORDER.includes(e) ? EFFORT_ORDER.indexOf(e) : EFFORT_ORDER.length);
const allEfforts = () => [...new Set(['low', 'medium', 'high', 'xhigh', 'max', 'ultra', ...catalog.flatMap((m) => m.efforts).filter((e) => e !== 'none')])].sort((a, b) => rank(a) - rank(b));

let catalog = [];      // [{id, name, default, effort, efforts}] — shared by every picker
let catalogAt = 0;
const pickers = new Set();
const defaultModel = () => catalog.find((m) => m.default) || null;

// "Claude Haiku 5.5 · Claude Code" → provider "Claude Code", short "Claude Haiku 5.5"
const provider = (m) => m.name.includes(' · ') ? m.name.split(' · ').slice(1).join(' · ') : '';
const short = (m) => m.name.split(' · ')[0];

export async function loadCatalog(force) {
  if (!force && catalog.length && Date.now() - catalogAt < 60000) return;
  if (!force && !catalog.length && Date.now() - catalogAt < 5000) return;
  catalogAt = Date.now();
  try { catalog = await api('GET', '/models'); } catch { catalog = []; }
  for (const p of pickers) p.redraw();
}
setInterval(() => { if (!document.hidden && pickers.size && !catalog.length) loadCatalog(true); }, 15000);

/**
 * Fills `modelBox` and `effortBox`. `up` opens the popover above the button (for the drawer footer).
 * Returns {model, effort, set(model, effort), destroy()}; '' means "agent default" / "model default".
 * onChange fires only for the user's own choices, not for set().
 */
export function createPicker(modelBox, effortBox, { up = false, onChange } = {}) {
  let model = '', effort = '';
  let pop = null;          // open popover state
  let modelRaw = '';

  const effective = () => (model ? catalog.find((m) => m.id === model) : defaultModel()) || null;

  const pv = el('span', { class: 'pv' });
  const pickBtn = el('button', { type: 'button', class: 'pick', title: 'Model', onclick: togglePop, 'aria-haspopup': 'listbox' },
    el('span', { class: 'lbl', textContent: 'Model' }), pv, el('span', { class: 'caret', textContent: '▾' }));
  modelBox.replaceChildren(pickBtn);

  function renderModel() {
    const cur = model ? catalog.find((m) => m.id === model) : null;
    const d = defaultModel();
    pv.textContent = model ? (cur ? cur.name : model) : d ? 'Default · ' + short(d) : 'Default';
    pickBtn.title = 'Model: ' + pv.textContent;
  }

  function items(q) {
    q = q.trim().toLowerCase();
    const out = [];
    const d = defaultModel();
    if (!q || 'default'.includes(q)) out.push({ value: '', label: 'Default', sub: d ? short(d) : '' });
    const groups = new Map();
    for (const m of catalog) {
      if (q && !(m.name + ' ' + m.id).toLowerCase().includes(q)) continue;
      const g = provider(m);
      if (!groups.has(g)) groups.set(g, []);
      groups.get(g).push(m);
    }
    for (const [g, list] of groups)
      for (const [k, m] of list.entries())
        out.push({ value: m.id, label: g ? short(m) : m.name, sub: m.id !== m.name && !g ? m.id : '', group: k === 0 && g ? g : null });
    if (q && modelRaw && !catalog.some((m) => m.id === modelRaw)) out.push({ value: modelRaw, label: `Use “${modelRaw}”`, custom: true });
    return out;
  }

  function togglePop() {
    if (pop) { closePop(); return; }
    loadCatalog(!catalog.length);
    const input = el('input', { class: 'popfilter', placeholder: catalog.length ? 'Filter or type a model…' : 'Model name (empty = default)', spellcheck: false });
    const list = el('div', { class: 'poplist', role: 'listbox' });
    const panel = el('div', { class: 'pop' + (up ? ' up' : '') }, input, list);
    pop = { panel, input, list, active: 0, shown: [] };
    modelBox.append(panel);
    const draw = () => {
      modelRaw = input.value.trim();
      pop.shown = items(input.value);
      pop.active = Math.min(pop.active, Math.max(pop.shown.length - 1, 0));
      list.replaceChildren(...pop.shown.map((it, idx) => el('div', {},
        it.group ? el('div', { class: 'pgroup', textContent: it.group }) : null,
        el('div', { class: 'popitem' + (idx === pop.active ? ' active' : '') + (it.value === model ? ' on' : ''), role: 'option',
          onmousemove: () => { if (pop.active !== idx) { pop.active = idx; draw(); } },
          onclick: () => choose(it.value) },
          el('span', { class: 'pl', textContent: it.label }),
          it.sub ? el('span', { class: 'ps mono', textContent: it.sub }) : null,
          it.value === model ? el('span', { class: 'ck', textContent: '✓' }) : null))));
      list.querySelector('.active')?.scrollIntoView({ block: 'nearest' });
    };
    input.addEventListener('input', () => { pop.active = 0; draw(); });
    input.addEventListener('keydown', (e) => {
      if (e.key === 'ArrowDown') { e.preventDefault(); pop.active = Math.min(pop.active + 1, pop.shown.length - 1); draw(); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); pop.active = Math.max(pop.active - 1, 0); draw(); }
      else if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); const it = pop.shown[pop.active]; if (it) choose(it.value); else choose(input.value.trim()); }
      else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closePop(); }
    });
    pop.draw = draw;
    draw();
    input.focus();
  }
  function closePop() {
    pop?.panel.remove();
    pop = null;
    modelRaw = '';
  }
  function choose(value) {
    model = value;
    closePop();
    renderModel();
    renderEffort();
    onChange?.();
  }
  const away = (e) => { if (pop && !modelBox.contains(e.target)) closePop(); };
  document.addEventListener('click', away);

  function renderEffort() {
    const m = effective();
    const supported = m ? new Set(m.efforts) : null; // null = unknown model: allow everything
    if (effort && supported && !supported.has(effort)) effort = '';
    const none = !!m && !m.efforts.length;
    effortBox.classList.toggle('off', none);
    effortBox.title = none ? 'This model has no configurable reasoning effort' : 'Reasoning effort';
    const seg = (value, label, tip, disabled) => el('button', {
      type: 'button', class: 'segbtn' + (effort === value ? ' on' : ''), title: tip || (disabled ? 'Not supported by this model' : ''),
      'aria-pressed': String(effort === value), disabled,
      onclick: () => { effort = value; renderEffort(); onChange?.(); },
    }, label);
    const all = allEfforts();
    effortBox.replaceChildren(
      seg('', 'Default', m?.effort && m.effort !== 'none' ? 'Model default: ' + m.effort : '', false),
      ...all.map((o) => seg(o, o, '', !!supported && !supported.has(o))));
    effortBox.style.setProperty('--n', String(all.length));
  }

  const self = {
    get model() { return model; },
    get effort() { return effort; },
    set(m, e) { if ((m || '') === model && (e || '') === effort) return; model = m || ''; effort = e || ''; renderModel(); renderEffort(); },
    redraw() { renderModel(); renderEffort(); pop?.draw(); },
    destroy() { closePop(); document.removeEventListener('click', away); pickers.delete(self); },
  };
  pickers.add(self);
  renderModel();
  renderEffort();
  loadCatalog(false);
  return self;
}
