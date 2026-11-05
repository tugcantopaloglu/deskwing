// DOM-controller fixtures. No live OBS, file chooser, app launch, or browser.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const source = fs.readFileSync(path.join(__dirname, '../companion/wwwroot/macro-editor.js'), 'utf8');
let passed = 0;
function check(value, label) { assert.ok(value, label); passed++; console.log('PASS ' + label); }

function fixture(action, api = async () => ({})) {
  let focused = null, changes = 0;
  class Node {
    constructor(tag) {
      this.tagName = tag.toUpperCase(); this.children = []; this.parentNode = null;
      this.dataset = {}; this.attrs = {}; this.className = ''; this.value = ''; this.text = '';
      this.disabled = false; this.checked = false;
      this.classList = {
        add: name => { this.className = [...new Set((this.className + ' ' + name).trim().split(/\s+/))].join(' '); },
        remove: name => { this.className = this.className.split(/\s+/).filter(item => item !== name).join(' '); }
      };
    }
    get isConnected() { return this === body || !!this.parentNode?.isConnected; }
    append(...nodes) { for (const node of nodes) { node.parentNode = this; this.children.push(node); } }
    replaceChildren(...nodes) { for (const node of this.children) node.parentNode = null; this.children = []; this.text = ''; this.append(...nodes); }
    set textContent(value) { for (const node of this.children) node.parentNode = null; this.children = []; this.text = String(value); }
    get textContent() { return this.text + this.children.map(child => child.textContent).join(''); }
    setAttribute(name, value) { this.attrs[name] = String(value); }
    getAttribute(name) { return this.attrs[name] ?? null; }
    setCustomValidity(value) { this.validationMessage = value; }
    reportValidity() { return !this.validationMessage && !(this.required && !this.value); }
    focus() { focused = this; }
    querySelector(selector) { return walk(this).find(node => matches(node, selector)) || null; }
    querySelectorAll(selector) { return walk(this).filter(node => matches(node, selector)); }
  }
  const body = new Node('body'), container = new Node('div'), add = new Node('button');
  container.id = 'macro-steps'; add.id = 'add-step'; body.append(container, add);
  function walk(root) { return root.children.flatMap(node => [node, ...walk(node)]); }
  function matches(node, selector) {
    if (selector.includes(',')) return selector.split(',').some(part => matches(node, part.trim()));
    if (selector[0] === '#') return node.id === selector.slice(1);
    if (selector[0] === '.') return node.className.split(/\s+/).includes(selector.slice(1));
    if (selector[0] === '[') {
      return [...selector.matchAll(/\[([^=\]]+)(?:="([^"]*)")?\]/g)].every(([, key, value]) => {
        const actual = key.startsWith('data-') ? node.dataset[key.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase())] : node.attrs[key];
        return value === undefined ? actual !== undefined : actual === value;
      });
    }
    return node.tagName === selector.toUpperCase();
  }
  const calls = [];
  const context = vm.createContext({
    window: {}, document: { createElement: tag => new Node(tag), querySelector: selector => body.querySelector(selector) },
    WeakMap, Set, Map, String, Number, Array, Object, JSON, Error, URLSearchParams,
    structuredClone, crypto, console, kinds: {}, markDirty: () => changes++, api
  });
  vm.runInContext(source, context);
  function mount(next = action) {
    return context.window.DeckMacroEditor.mount(container, next, {
      changed: () => changes++, addButton: add,
      api: async (route, payload, timeout) => { calls.push({ route, payload, timeout }); return api(route, payload, timeout); }
    });
  }
  mount();
  const nodes = () => walk(body);
  const field = (step, key) => container.querySelector(`[data-step="${step}"][data-field="${key}"]`);
  const byText = (text, within = container) => walk(within).find(node => node.tagName === 'BUTTON' && node.textContent === text);
  const byLabel = label => nodes().find(node => node.attrs['aria-label'] === label);
  const input = (step, key, value) => { const node = field(step, key); node.value = value; node.oninput(); return node; };
  return { context, action, body, container, add, nodes, field, byText, byLabel, input, calls, mount, get changes() { return changes; }, get focused() { return focused; } };
}
const step = (kind, target = '', value = '', steps = []) => ({ id: crypto.randomUUID().replaceAll('-', ''), label: kind + ' step', kind, target, value, icon: 'bolt', color: 'violet', confirm: false, steps, soundpadKey: '' });
const macro = steps => step('macro', '', '', steps);

async function run() {
  let action = macro([step('scene', 'Gameplay'), step('delay', '1500'), step('media', 'Video', 'stop'), step('macro', '', '', [step('delay', '500'), step('hotkey', 'CTRL+M')])]);
  let f = fixture(action);
  check(f.container.textContent.includes('3 actions · 2 s planned delays + action time'), 'timing sums nested delays without promising action duration');
  check(f.field('1', 'target').type === 'number' && f.field('1', 'target').max === '10000', 'delays use a bounded numeric field');
  check(f.field('2', 'value').tagName === 'SELECT', 'media operation has a typed choice');
  check(!f.container.textContent.includes('Target / file / milliseconds'), 'generic overloaded target labels are absent');
  f.input('1', 'target', '10001');
  check(f.field('1', 'target').attrs['aria-invalid'] === 'true' && f.context.window.DeckMacroEditor.summary(action).invalidDelays === 1, 'invalid delay is visibly marked and excluded from duration');
  await f.byText('Check step', f.container.querySelector('[data-macro-step="1"]')).onclick();
  check(!f.calls.length && f.container.textContent.includes('Fix the highlighted field'), 'invalid typed fields are rejected before sending a check request');
  f.input('1', 'target', '1250');
  check(f.container.textContent.includes('1.75 s planned delays') && f.field('1', 'target').validationMessage === '', 'valid edits update duration and clear invalid state');
  const originalOrder = action.steps.map(item => item.id);
  f.byLabel('Move step 2 earlier').onclick();
  check(action.steps[0].id === originalOrder[1] && f.focused === f.field('0', 'kind'), 'keyboard reorder preserves identity and focuses the moved step');
  const group = action.steps[3];
  f.byLabel('Duplicate step 4').onclick();
  const copy = action.steps[4];
  check(copy.id !== group.id && copy.steps[0].id !== group.steps[0].id && copy.steps[0].target === group.steps[0].target, 'duplicating a group regenerates every identity while preserving settings');
  const changesBefore = f.changes;
  f.byLabel('Remove step 5').onclick();
  check(action.steps.length === 4 && f.changes === changesBefore + 1, 'remove is one editor-history mutation');
  const first = action.steps[0], second = action.steps[1];
  f.byLabel('Drag step 1 to reorder').ondragstart({ dataTransfer: { setData() {} } });
  const row = f.container.querySelector('[data-macro-step="1"]');
  let prevented = false;
  row.ondrop({ preventDefault() { prevented = true; }, stopPropagation() {} });
  check(prevented && action.steps[0] === second && action.steps[1] === first, 'drag reorder moves only within the current group');
  f.byLabel('Drag step 1 to reorder').ondragstart({ dataTransfer: { setData() {} } });
  const nested = f.container.querySelector('[data-macro-step="3.0"]');
  let stopped = false;
  nested.ondrop({ preventDefault() { throw Error('Cross-group drop must not be accepted'); }, stopPropagation() { stopped = true; } });
  check(stopped && action.steps[0] === second && action.steps[3].steps.length === 2, 'cross-group drag cannot bubble into an ancestor reorder');
  check(!f.field('3.0', 'kind').textContent.includes('Hold keyboard'), 'held shortcuts are not offered inside macros');

  action = macro([step('soundpadPlay', '12', 'mic')]); action.steps[0].soundpadKey = 'A'.repeat(64);
  f = fixture(action); f.input('0', 'target', '13');
  check(action.steps[0].soundpadKey === '', 'editing a Soundpad index clears the old stable binding');
  check(f.field('0', 'value').tagName === 'SELECT' && f.field('0', 'value').value === 'mic', 'Soundpad output preserves its explicit selection');
  let select = f.field('0', 'kind'); select.value = 'delay'; select.onchange();
  check(action.steps[0].kind === 'delay' && action.steps[0].target === '500' && action.steps[0].value === '', 'changing action kind clears incompatible fields and gives a valid delay default');
  f = fixture(macro([step('streamStart'), step('hotkey', 'CTRL+M')]));
  check(f.container.textContent.includes('always requires confirmation'), 'broadcast steps explain mandatory preflight confirmation');
  const checkboxes = f.nodes().filter(node => node.type === 'checkbox');
  check(checkboxes.length === 1, 'mandatory broadcast confirmation cannot be turned off');
  checkboxes[0].checked = true; checkboxes[0].onchange();
  check(f.action.steps[1].confirm === true, 'optional step confirmation is preserved in the macro data');

  f = fixture(macro(Array.from({ length: 30 }, () => step('delay', '0'))));
  check(f.add.disabled && f.nodes().filter(node => node.textContent === 'Duplicate' && node.tagName === 'BUTTON').every(node => node.disabled), '30-step limit disables both additions and duplicates');
  f.add.onclick();
  check(f.action.steps.length === 30, 'programmatic add also respects the step limit');
  f = fixture(macro([step('macro', '', '', [step('macro', '', '', [step('scene', 'A')])])]));
  check(!f.field('0.0.0', 'kind').textContent.includes('Action group'), 'third-level steps cannot create a fourth-level group');
  f = fixture(macro([step('macro', '', '', [step('macro', '', '', [step('macro', '', '', [step('scene', 'A')])])])]));
  check(f.container.textContent.includes('too deeply nested') && f.action.steps[0].steps[0].steps[0].steps.length === 1, 'invalid imported nesting stays intact with an actionable message');

  let release;
  action = macro([step('sound', 'C:\\old.wav')]);
  f = fixture(action, () => new Promise(resolve => { release = resolve; }));
  let pending = f.byText('Choose sound file').onclick();
  check(f.context.window.DeckMacroEditor.hasPending(), 'active picker exposes a save guard while its response is pending');
  check(f.calls[0].route === '/api/admin/pick-file' && f.calls[0].payload.kind === 'sound' && f.calls[0].timeout === 0, 'file chooser requests the existing local picker without an execution call');
  f.input('0', 'target', 'C:\\manual.wav'); release({ path: 'C:\\picked.wav' }); await pending;
  check(action.steps[0].target === 'C:\\manual.wav', 'late file chooser cannot overwrite an intervening typed target');
  check(f.container.textContent.includes('Kept your typed value') && !f.container.textContent.includes('Checking…'), 'discarded file selection clears its pending status with an explanation');
  check(!f.context.window.DeckMacroEditor.hasPending(), 'finished picker releases the save guard');
  pending = f.byText('Choose sound file').onclick();
  f.mount(macro([step('delay', '5')]));
  check(!f.context.window.DeckMacroEditor.hasPending(), 'abandoned editor request does not lock a newer editor');
  release({ path: 'C:\\late.wav' }); await pending;
  check(action.steps[0].target === 'C:\\manual.wav' && f.field('0', 'target').value === '5', 'late file chooser cannot mutate a different action after editor navigation');

  f = fixture(macro([step('source', 'Scene & one', '')]), async () => ({ scenes: ['Scene & one'], inputs: ['Mic'], sources: ['Camera <safe>'], filters: [] }));
  await f.byText('Find OBS names').onclick();
  check(f.calls[0].route === '/api/admin/obs/targets?scene=Scene+%26+one', 'target discovery encodes exact scene names');
  check(f.nodes().some(node => node.tagName === 'OPTION' && node.value === 'Camera <safe>'), 'discovered source names populate the source field suggestions');
  check(!f.nodes().some(node => node.tagName === 'IMG'), 'untrusted target names are text, never markup');
  f = fixture(macro([step('filter', 'Camera', 'Color')]), async () => ({ scenes: ['Main'], inputs: ['Camera'], sources: [], filters: ['Color'] }));
  await f.byText('Find OBS names').onclick();
  check(f.calls[0].route.endsWith('?source=Camera') && f.field('0', 'value').attrs.list, 'filter discovery scopes suggestions to its selected source');
  f = fixture(macro([step('scene', 'Saved scene')]), async () => { throw new Error('OBS is disconnected.'); });
  await f.byText('Find OBS names').onclick();
  check(f.action.steps[0].target === 'Saved scene' && f.container.textContent.includes('Your target is unchanged'), 'offline discovery retains exact-name editing and explains recovery');

  action = macro([step('hotkey', 'CTRL+M')]);
  f = fixture(action, async () => ({ ok: true, problems: [], note: 'Validation only; no action ran.' }));
  await f.byText('Check step').onclick();
  check(f.calls.length === 1 && f.calls[0].route === '/api/admin/action-check' && f.calls[0].payload.action.target === 'CTRL+M', 'check step sends only validation with a snapshot of the edited step');
  check(f.container.textContent.includes('Validation only; no action ran.'), 'check feedback states that nothing executed');
  f.input('0', 'target', 'CTRL+SHIFT+M');
  check(!f.container.textContent.includes('Target checks passed') && f.container.textContent.includes('Values changed'), 'changing a checked step invalidates its old success feedback');
  f = fixture(action, () => new Promise(resolve => { release = resolve; }));
  pending = f.byText('Check step').onclick(); f.input('0', 'target', 'CTRL+N'); release({ ok: true, note: 'Stale success' }); await pending;
  check(!f.container.textContent.includes('Stale success'), 'late validation cannot claim success for an edited target');
  check(f.container.textContent.includes('Check the current values again'), 'stale validation clears pending status and offers a retry');
  check(!f.calls.some(call => /^\/api\/(?:action|computer\/action|soundpad\/play)/.test(call.route)), 'composer never calls control execution endpoints');
  console.log(`${passed} macro editor checks passed. No real controls executed.`);
}
run().catch(error => { console.error(error); process.exitCode = 1; });
