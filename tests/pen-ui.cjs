'use strict';
// Pure state/pointer tests: no browser, physical pen, handwriting service or PC action.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');

const source = path.resolve(__dirname, '../companion/wwwroot/pen.js');
const eventHandlers = new Map();
const stored = new Map();
const noop = () => {};
const document = {
  readyState: 'loading', hidden: false,
  addEventListener(type, listener) { eventHandlers.set('document:' + type, listener); },
  getElementById() { return null; }, querySelector() { return null; }, querySelectorAll() { return []; },
  body: { classList: { add: noop, remove: noop, toggle: noop } }
};
const storage = {
  getItem(key) { return stored.has(key) ? stored.get(key) : null; },
  setItem(key, value) { stored.set(key, String(value)); }, removeItem(key) { stored.delete(key); }
};
const context = {
  console, document, localStorage: storage, JSON, Math, Date, Number, String, Object, Array,
  Set, Map, Uint8Array, TextEncoder, Blob, URL, Event,
  setTimeout: () => 1, clearTimeout: noop,
  requestAnimationFrame: () => 1, cancelAnimationFrame: noop,
  addEventListener(type, listener) { eventHandlers.set('window:' + type, listener); },
  dispatchEvent: noop, devicePixelRatio: 1,
  native: false,
  notice: noop
};
context.window = context;
context.globalThis = context;
vm.createContext(context);
vm.runInContext(fs.readFileSync(source, 'utf8'), context, { filename: source });
const core = context.DeskPenCore;
assert.ok(core, 'pen module exposes its state and pointer helpers');

let passed = 0;
function check(label, test) { test(); passed++; console.log('PASS ' + label); }
function plain(value) { return JSON.parse(JSON.stringify(value)); }
function goodDocument() {
  return { version: 1, strokes: [{ tool: 'pen', color: '#23313f', width: 3,
    points: [[0.1, 0.2, 0.5], [0.6, 0.7, 0.9]] }],
    notes: 'Meeting notes\nTürkçe: çığ şüphe', penOnly: true, updatedAt: '2026-11-05T12:00:00.000Z' };
}
function rejects(label, mutate) {
  check(label, () => { const doc = goodDocument(); mutate(doc); assert.throws(() => core.normalizeDocument(doc)); });
}

check('new scratchpads ignore finger drawing by default', () => {
  const doc = core.createDocument();
  assert.equal(doc.version, 1); assert.equal(doc.penOnly, true);
  assert.equal(doc.notes, ''); assert.equal(doc.strokes.length, 0);
});
check('valid ink and Unicode notes survive normalization', () => {
  const doc = goodDocument(), result = core.normalizeDocument(doc);
  assert.deepEqual(plain(result.strokes), doc.strokes);
  assert.equal(result.notes, doc.notes); assert.equal(result.penOnly, true);
});
check('storage normalization clones mutable stroke data', () => {
  const doc = goodDocument(), result = core.normalizeDocument(doc);
  doc.strokes[0].points[0][0] = 0.99;
  assert.equal(result.strokes[0].points[0][0], 0.1);
});
check('unknown fields cannot become executable content or pollute prototypes', () => {
  const doc = JSON.parse(JSON.stringify(goodDocument()).replace('"version":1', '"version":1,"__proto__":{"penPolluted":true},"onclick":"alert(1)"'));
  const result = core.normalizeDocument(doc);
  assert.equal(Object.prototype.penPolluted, undefined);
  assert.equal(Object.hasOwn(result, '__proto__'), false);
  assert.equal(Object.hasOwn(result, 'onclick'), false);
});
check('empty scratchpads are valid', () => {
  const result = core.normalizeDocument(core.createDocument());
  assert.equal(result.strokes.length, 0);
});
for (const value of [null, false, 1, 'saved drawing', [], {}]) {
  check('non-document storage is rejected: ' + JSON.stringify(value), () => assert.throws(() => core.normalizeDocument(value)));
}
rejects('future document versions are rejected instead of misread', d => d.version = 99);
rejects('stroke arrays are required', d => d.strokes = {});
rejects('unrecognized drawing tools are rejected', d => d.strokes[0].tool = 'script');
rejects('CSS and URL color payloads are rejected', d => d.strokes[0].color = 'url(https://example.invalid/pixel)');
rejects('negative brush width is rejected', d => d.strokes[0].width = -1);
rejects('unbounded brush width is rejected', d => d.strokes[0].width = 999999);
rejects('non-finite brush width is rejected', d => d.strokes[0].width = Infinity);
rejects('missing point coordinates are rejected', d => d.strokes[0].points = [[0.1]]);
rejects('NaN point coordinates are rejected', d => d.strokes[0].points[0][0] = NaN);
rejects('infinite pressure is rejected', d => d.strokes[0].points[0][2] = Infinity);
rejects('coordinates outside the normalized sheet are rejected', d => d.strokes[0].points[0][0] = 1.1);
rejects('negative pressure is rejected', d => d.strokes[0].points[0][2] = -0.1);
rejects('pressure over the hardware range is rejected', d => d.strokes[0].points[0][2] = 1.1);
rejects('notes must be text', d => d.notes = { html: '<script>' });
rejects('overlong notes are rejected', d => d.notes = 'a'.repeat(core.constants.MAX_NOTE + 1));
rejects('excessive stroke counts are rejected', d => d.strokes = Array.from({ length: core.constants.MAX_STROKES + 1 }, () => ({ tool: 'pen', color: '#000000', width: 2, points: [[0.2, 0.3, 0.5]] })));
rejects('excessive point counts are rejected', d => d.strokes[0].points = Array.from({ length: core.constants.MAX_POINTS + 1 }, () => [0.2, 0.3, 0.5]));

check('pen contact draws with finger rejection enabled', () => assert.equal(core.mayDraw('pen', true, true), true));
check('mouse remains usable on the PC', () => assert.equal(core.mayDraw('mouse', true, false), true));
check('fingers cannot draw in pen-only mode', () => assert.equal(core.mayDraw('touch', true, false), false));
check('finger drawing is available when explicitly enabled', () => assert.equal(core.mayDraw('touch', false, false), true));
check('nearby pen still rejects palm contact when fingers are enabled', () => assert.equal(core.mayDraw('touch', false, true), false));

check('pointer coordinates are normalized to the visible canvas', () => {
  const p = core.makePoint({ clientX: 60, clientY: 70, pressure: 0.8, pointerType: 'pen', buttons: 1 }, { left: 10, top: 20, width: 200, height: 100 });
  assert.deepEqual(plain(p), [0.25, 0.5, 0.8]);
});
check('captured points beyond the canvas stay within its sheet', () => {
  const p = core.makePoint({ clientX: -50, clientY: 500, pressure: 0.8, pointerType: 'pen', buttons: 1 }, { left: 0, top: 0, width: 100, height: 100 });
  assert.equal(p[0], 0); assert.equal(p[1], 1);
});
check('hardware without pressure produces a finite visible mark', () => {
  const p = core.makePoint({ clientX: 50, clientY: 50, pointerType: 'mouse', buttons: 1 }, { left: 0, top: 0, width: 100, height: 100 });
  assert.ok(Number.isFinite(p[2]) && p[2] > 0 && p[2] <= 1);
});
check('pressure increases stroke thickness without hiding light strokes', () => {
  const light = core.strokeWidth(8, 0), firm = core.strokeWidth(8, 1);
  assert.ok(Number.isFinite(light) && light > 0);
  assert.ok(Number.isFinite(firm) && firm > light);
});

check('empty history cannot undo or redo', () => {
  const history = core.createHistory([]);
  assert.equal(history.canUndo, false); assert.equal(history.canRedo, false);
  assert.deepEqual(plain(history.undo()), []); assert.deepEqual(plain(history.redo()), []);
});
check('clear drawing can be undone and redone without affecting the original strokes', () => {
  const strokes = goodDocument().strokes, history = core.createHistory(strokes);
  history.commit([]); assert.equal(history.current.length, 0);
  assert.equal(history.canUndo, true);
  assert.deepEqual(plain(history.undo()), strokes);
  assert.equal(history.canRedo, true);
  assert.deepEqual(plain(history.redo()), []);
});
check('drawing after undo discards the abandoned redo branch', () => {
  const history = core.createHistory([]);
  history.commit(['first']); history.commit(['first', 'second']); history.undo();
  history.commit(['first', 'replacement']);
  assert.equal(history.canRedo, false);
  assert.deepEqual(plain(history.redo()), ['first', 'replacement']);
});
check('history is bounded to sixty previous states', () => {
  const history = core.createHistory([]);
  for (let i = 1; i <= 100; i++) history.commit([i]);
  let count = 0;
  while (history.canUndo) { history.undo(); count++; }
  assert.equal(count, 60); assert.deepEqual(plain(history.current), [40]);
});

eventHandlers.get('document:DOMContentLoaded')();
function pointer(type, pointerType, pointerId) {
  return { type, pointerType, pointerId, prevented: false, stopped: false,
    preventDefault() { this.prevented = true; }, stopImmediatePropagation() { this.stopped = true; } };
}
check('touch controls are not globally disabled before using the pen', () => {
  const event = pointer('pointerdown', 'touch', 20);
  eventHandlers.get('document:pointerdown')(event);
  assert.equal(event.prevented, false); assert.equal(event.stopped, false);
});
check('pen contact suppresses a simultaneous palm contact through its full gesture', () => {
  eventHandlers.get('document:pointerdown')(pointer('pointerdown', 'pen', 1));
  for (const type of ['pointerdown', 'pointermove', 'pointerup']) {
    const event = pointer(type, 'touch', 2);
    eventHandlers.get('document:' + type)(event);
    assert.equal(event.prevented, true); assert.equal(event.stopped, true);
  }
});
check('suspending the app releases pen proximity and does not block a future touch', () => {
  context.DeskPen.suspend();
  const event = pointer('pointerdown', 'touch', 3);
  eventHandlers.get('document:pointerdown')(event);
  assert.equal(event.prevented, false);
});

function textField(value = 'Original', maxLength = -1) {
  return { tagName: 'TEXTAREA', type: 'textarea', isConnected: true, disabled: false, readOnly: false,
    value, maxLength, events: [], focused: false, closest: () => null,
    dispatchEvent(event) { this.events.push(event.type); }, focus() { this.focused = true; } };
}
async function asyncCheck(label, test) { await test(); passed++; console.log('PASS ' + label); }
function canvasFixture(seed, options = {}) {
  const listeners = new Map(), windowListeners = new Map(), data = new Map(seed ? [['deskwing.pen.v1', JSON.stringify(seed)]] : []), downloads = [], timers = new Map();
  for (const [key, value] of Object.entries(options.storage || {})) data.set(key, value);
  let timerId = 0;
  const drawing = { save: noop, restore: noop, beginPath: noop, arc: noop, fill: noop, moveTo: noop, lineTo: noop, stroke: noop, clearRect: noop, fillRect: noop, drawImage: noop };
  class Node {
    constructor(tag) {
      this.tagName = tag.toUpperCase(); this.children = []; this.dataset = {}; this.handlers = new Map();
      this.attributes = {}; this.hidden = false; this.disabled = false; this.readOnly = false; this.value = ''; this.maxLength = -1;
      this.style = { setProperty(key, value) { this[key] = value; }, removeProperty(key) { delete this[key]; } };
      this.captures = new Set(); this.classes = new Set();
      this.classList = { add: c => this.classes.add(c), remove: c => this.classes.delete(c), contains: c => this.classes.has(c), toggle: (c, value) => value ? this.classes.add(c) : this.classes.delete(c) };
    }
    get isConnected() { return this === uiDocument.body || !!this.parentNode?.isConnected; }
    get firstChild() { return this.children[0]; }
    append(...nodes) { for (const node of nodes) { if (node.parentNode) node.remove(); node.parentNode = this; this.children.push(node); } }
    appendChild(node) { this.append(node); return node; }
    replaceChildren(...nodes) { for (const node of this.children) node.parentNode = null; this.children = []; this.append(...nodes); }
    remove() { if (this.parentNode) this.parentNode.children = this.parentNode.children.filter(node => node !== this); this.parentNode = null; }
    addEventListener(type, callback) { if (!this.handlers.has(type)) this.handlers.set(type, []); this.handlers.get(type).push(callback); }
    removeEventListener(type, callback) { this.handlers.set(type, (this.handlers.get(type) || []).filter(item => item !== callback)); }
    setAttribute(key, value) { this.attributes[key] = String(value); }
    removeAttribute(key) { delete this.attributes[key]; }
    matches(selector) { return selector.startsWith('#') ? this.id === selector.slice(1) : selector.startsWith('.') ? (this.className || '').split(' ').includes(selector.slice(1)) || this.classes.has(selector.slice(1)) : selector === '[hidden]' ? this.hidden : selector === '[data-pen-handwrite]' ? !!this.dataset.penHandwrite : selector.split(',').some(tag => tag.trim().toUpperCase() === this.tagName); }
    closest(selector) { return this.matches(selector) ? this : this.parentNode?.closest(selector) || null; }
    querySelector(selector) { return walk(this).slice(1).find(node => node.matches(selector)) || null; }
    querySelectorAll(selector) { return walk(this).slice(1).filter(node => node.matches(selector)); }
    contains(node) { return walk(this).includes(node); }
    getBoundingClientRect() { return { left: 0, top: 0, width: parseFloat(this.style.width) || options.viewport?.width || 800, height: parseFloat(this.style.height) || options.viewport?.height || 500 }; }
    setPointerCapture(id) { this.captures.add(id); }
    hasPointerCapture(id) { return this.captures.has(id); }
    releasePointerCapture(id) { this.captures.delete(id); }
    getContext() { return drawing; }
    toDataURL() { return 'data:image/png;base64,fixture'; }
    focus() { uiDocument.activeElement = this; }
    select() { this.selected = true; }
    dispatchEvent(event) { for (const callback of this.handlers.get(event.type) || []) callback(event); if (typeof this['on' + event.type] === 'function') this['on' + event.type](event); return true; }
    async click() { if (this.tagName === 'A') downloads.push({ name: this.download, href: this.href }); for (const callback of this.handlers.get('click') || []) await callback({ target: this, preventDefault: noop }); }
    async requestFullscreen() { if (options.fullscreenRejected) throw new Error('Fullscreen unavailable'); uiDocument.fullscreenElement = this; await emitDocument('fullscreenchange'); }
  }
  const walk = node => [node, ...node.children.flatMap(walk)];
  const uiDocument = {
    hidden: false, readyState: 'loading', body: null, fullscreenElement: null,
    createElement: tag => new Node(tag),
    getElementById(id) { return walk(this.body).find(node => node.id === id) || null; },
    querySelector(selector) { return walk(this.body).find(node => node.matches(selector)) || null; },
    querySelectorAll(selector) { return walk(this.body).filter(node => node.matches(selector)); },
    addEventListener(type, callback) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(callback); },
    async exitFullscreen() { this.fullscreenElement = null; await emitDocument('fullscreenchange'); }
  };
  uiDocument.body = new Node('body');
  uiDocument.documentElement = uiDocument.body;
  async function emitDocument(type, event = {}) { for (const listener of listeners.get(type) || []) await listener({ type, preventDefault: noop, ...event }); }
  async function emitWindow(type, event = {}) { for (const listener of windowListeners.get(type) || []) await listener({ type, preventDefault: noop, ...event }); }
  const runtime = { console, document: uiDocument, Event, navigator: {}, native: false, notice: noop, crypto: webcrypto, TextEncoder,
    localStorage: { getItem: key => data.get(key) || null, setItem: (key, value) => data.set(key, value), removeItem: key => data.delete(key) },
    setTimeout(callback, delay = 0) { timers.set(++timerId, { callback, delay }); return timerId; }, clearTimeout: id => timers.delete(id),
    requestAnimationFrame: callback => { callback(); return 1; }, cancelAnimationFrame: noop,
    addEventListener(type, callback) { if (!windowListeners.has(type)) windowListeners.set(type, []); windowListeners.get(type).push(callback); },
    devicePixelRatio: 1, innerWidth: options.viewport?.width || 800, innerHeight: options.viewport?.height || 500,
    confirmAction: options.confirmAction || (async () => true) };
  if (options.api) runtime.api = options.api;
  runtime.window = runtime;
  vm.createContext(runtime); vm.runInContext(fs.readFileSync(source, 'utf8'), runtime, { filename: source });
  for (const initialize of listeners.get('DOMContentLoaded') || []) initialize();
  runtime.DeskPen.setActive(true);
  function dispatch(type, pointerType = 'pen', pointerId = 1, x = 50, y = 50) {
    const canvas = uiDocument.getElementById('pen-canvas');
    const event = { ...pointer(type, pointerType, pointerId), target: canvas, button: 0, buttons: type === 'pointerup' ? 0 : 1, clientX: x, clientY: y, pressure: 0.6 };
    for (const handler of listeners.get(type) || []) handler(event);
    if (!event.stopped) canvas.dispatchEvent(event);
    return event;
  }
  return { runtime, document: uiDocument, data, downloads, dispatch, emitDocument, emitWindow,
    saved: () => JSON.parse(data.get('deskwing.pen.v1') || 'null'),
    flush: delay => { for (const [id, timer] of [...timers.entries()]) if (delay === undefined || timer.delay === delay) { timers.delete(id); timer.callback(); } } };
}
async function canvasChecks() {
  check('pointer up commits a captured stroke and saves pressure samples', () => {
    const ui = canvasFixture();
    ui.dispatch('pointerdown'); ui.dispatch('pointermove', 'pen', 1, 100, 120); ui.dispatch('pointerup', 'pen', 1, 150, 160);
    const saved = ui.saved(); assert.equal(saved.strokes.length, 1); assert.ok(saved.strokes[0].points.length >= 3);
    assert.equal(saved.strokes[0].points[0][2], 0.6);
    assert.equal(ui.document.getElementById('pen-canvas').captures.size, 0);
  });
  check('pointer cancellation discards the in-progress stroke', () => {
    const ui = canvasFixture();
    ui.dispatch('pointerdown'); ui.dispatch('pointermove', 'pen', 1, 100, 120); ui.dispatch('pointercancel');
    ui.runtime.DeskPen.suspend(); assert.equal(ui.saved().strokes.length, 0);
    assert.equal(ui.document.getElementById('pen-canvas').captures.size, 0);
  });
  check('lost capture cannot leave a partial stroke or stuck drawing state', () => {
    const ui = canvasFixture();
    ui.dispatch('pointerdown'); ui.dispatch('lostpointercapture'); ui.runtime.DeskPen.suspend();
    assert.equal(ui.saved().strokes.length, 0);
  });
  check('finger contact cannot persist ink while Ignore fingers is enabled', () => {
    const ui = canvasFixture();
    ui.dispatch('pointerdown', 'touch'); ui.dispatch('pointermove', 'touch', 1, 100, 120); ui.dispatch('pointerup', 'touch');
    ui.runtime.DeskPen.suspend(); assert.equal(ui.saved().strokes.length, 0);
  });
  check('a pen arriving during an allowed finger stroke discards the finger stroke', () => {
    const seed = core.createDocument(); seed.penOnly = false;
    const ui = canvasFixture(seed);
    ui.dispatch('pointerdown', 'touch', 5); ui.dispatch('pointermove', 'touch', 5, 100, 120);
    ui.dispatch('pointerdown', 'pen', 6); ui.dispatch('pointerup', 'pen', 6, 200, 200);
    assert.equal(ui.saved().strokes.length, 1); assert.equal(ui.saved().strokes[0].points[0][0], 50 / 800);
  });
  check('typed notes are saved independently from drawing history', () => {
    const ui = canvasFixture(), notes = ui.document.getElementById('pen-notes');
    notes.value = 'Local script\nİyi yayınlar'; notes.oninput(); ui.flush();
    assert.equal(ui.saved().notes, notes.value); assert.equal(ui.saved().strokes.length, 0);
  });
  await asyncCheck('export refuses an unfinished stroke, then creates a named PNG after release', async () => {
    const ui = canvasFixture(), exportButton = ui.document.getElementById('pen-export');
    ui.dispatch('pointerdown'); await exportButton.click(); assert.equal(ui.downloads.length, 0);
    ui.dispatch('pointerup'); await exportButton.click();
    assert.equal(ui.downloads.length, 1); assert.equal(ui.downloads[0].name, 'DeskWing-sketch.png');
    assert.ok(ui.downloads[0].href.startsWith('data:image/png;')); assert.equal(ui.saved().strokes.length, 1);
  });
  await asyncCheck('native export uses its bounded local bridge without a browser download', async () => {
    const ui = canvasFixture(); let call;
    ui.runtime.native = true; ui.runtime.nativeRequest = async (...args) => { call = args; return { saved: true, cancelled: false }; };
    await ui.document.getElementById('pen-export').click();
    assert.equal(call[0], '/local/export-image'); assert.equal(call[1].name, 'DeskWing-sketch.png');
    assert.equal(call[3], 0); assert.equal(ui.downloads.length, 0);
  });
}
function inkDocument(notes) { return { ...plain(core.createDocument()), notes }; }
function inkServer(document, options = {}) {
  let version = 1, remote = plain(document), offline = false;
  const calls = [];
  const snapshot = () => ({ workspaceId: options.workspaceId || 'fixture-pc-ink', revision: 'r' + version, document: plain(remote), recoveryWarning: '' });
  return { calls, snapshot,
    get offline() { return offline; }, set offline(value) { offline = value; },
    change(document) { remote = plain(document); version++; },
    async api(route, body) {
      assert.equal(route, '/api/ink'); calls.push({ route, body: body === undefined ? undefined : plain(body) });
      if (offline) throw new Error('Fixture PC is offline.');
      if (body === undefined) return snapshot();
      if (body.expectedRevision !== 'r' + version) throw new Error('The PC copy changed. Sync again.');
      remote = plain(body.document); version++; return snapshot();
    }
  };
}
const writes = server => server.calls.filter(call => call.body !== undefined);
const notesIn = ui => ui.document.getElementById('pen-notes').value;
function editNotes(ui, text) { const field = ui.document.getElementById('pen-notes'); field.value = text; field.oninput(); ui.flush(350); }
async function waitUntil(predicate) { for (let i = 0; i < 100 && !predicate(); i++) await new Promise(setImmediate); assert.ok(predicate(), 'fixture async request reached the intended boundary'); }
async function syncChecks() {
  await asyncCheck('different initial local and PC ink require a choice without any write', async () => {
    const server = inkServer(inkDocument('PC notes')), ui = canvasFixture(inkDocument('Local notes'), { api: server.api });
    await ui.runtime.DeskPen.syncNow();
    assert.equal(writes(server).length, 0); assert.equal(notesIn(ui), 'Local notes');
    assert.equal(ui.document.getElementById('pen-sync-state').textContent, 'Two versions need your choice');
    assert.equal(ui.document.getElementById('pen-use-pc').closest('.pen-conflict').hidden, false);
  });
  await asyncCheck('an empty local scratchpad adopts the PC without an unnecessary write', async () => {
    const server = inkServer(inkDocument('PC notes')), ui = canvasFixture(undefined, { api: server.api });
    await ui.runtime.DeskPen.syncNow();
    assert.equal(notesIn(ui), 'PC notes'); assert.equal(ui.saved().notes, 'PC notes'); assert.equal(writes(server).length, 0);
    const meta = JSON.parse(ui.data.get('deskwing.pen.sync.v1'));
    assert.equal(meta.workspaceId, server.snapshot().workspaceId); assert.equal(meta.revision, 'r1'); assert.match(meta.hash, /^[a-f0-9]{64}$/);
  });
  await asyncCheck('existing local ink migrates into an empty PC with a revision check', async () => {
    const server = inkServer(inkDocument('')), ui = canvasFixture(inkDocument('Local notes'), { api: server.api });
    await ui.runtime.DeskPen.syncNow();
    assert.equal(writes(server).length, 1); assert.equal(writes(server)[0].body.expectedRevision, 'r1');
    assert.equal(server.snapshot().document.notes, 'Local notes'); assert.equal(notesIn(ui), 'Local notes');
  });
  await asyncCheck('offline local edits retry safely when the PC version is unchanged', async () => {
    const server = inkServer(inkDocument('Shared')), ui = canvasFixture(undefined, { api: server.api });
    await ui.runtime.DeskPen.syncNow(); server.offline = true; editNotes(ui, 'Offline edit'); await ui.runtime.DeskPen.syncNow();
    assert.equal(ui.saved().notes, 'Offline edit'); assert.equal(writes(server).length, 0);
    server.offline = false; await ui.runtime.DeskPen.syncNow();
    assert.equal(writes(server).length, 1); assert.equal(server.snapshot().document.notes, 'Offline edit');
  });
  await asyncCheck('offline edits cannot overwrite a PC copy changed in the meantime', async () => {
    const server = inkServer(inkDocument('Shared')), ui = canvasFixture(undefined, { api: server.api });
    await ui.runtime.DeskPen.syncNow(); server.offline = true; editNotes(ui, 'Local offline edit'); await ui.runtime.DeskPen.syncNow();
    server.change(inkDocument('Concurrent PC edit')); server.offline = false; await ui.runtime.DeskPen.syncNow();
    assert.equal(writes(server).length, 0); assert.equal(notesIn(ui), 'Local offline edit'); assert.equal(server.snapshot().document.notes, 'Concurrent PC edit');
    assert.equal(ui.document.getElementById('pen-sync-state').textContent, 'Two versions need your choice');
  });
  await asyncCheck('an in-flight read cannot replace a stroke that has begun', async () => {
    let resolveRead; const server = inkServer(inkDocument('Remote content'));
    const ui = canvasFixture(undefined, { api: (route, body) => body === undefined ? new Promise(resolve => { resolveRead = resolve; }) : server.api(route, body) });
    const pending = ui.runtime.DeskPen.syncNow();
    ui.dispatch('pointerdown'); ui.dispatch('pointermove', 'pen', 1, 120, 140);
    resolveRead(server.snapshot()); await pending;
    assert.equal(notesIn(ui), ''); ui.dispatch('pointerup', 'pen', 1, 180, 200);
    assert.equal(ui.saved().strokes.length, 1); assert.equal(ui.saved().notes, ''); assert.equal(writes(server).length, 0);
  });
  await asyncCheck('suspending during a read prevents its queued PC write', async () => {
    let resolveRead; const server = inkServer(inkDocument(''));
    const ui = canvasFixture(inkDocument('Unshared local'), { api: (route, body) => body === undefined ? new Promise(resolve => { resolveRead = resolve; }) : server.api(route, body) });
    const pending = ui.runtime.DeskPen.syncNow(); ui.runtime.DeskPen.suspend(); resolveRead(server.snapshot()); await pending;
    assert.equal(writes(server).length, 0); assert.equal(ui.saved().notes, 'Unshared local');
    await ui.runtime.DeskPen.syncNow(); assert.equal(writes(server).length, 0);
  });
  await asyncCheck('a visible PC window keeps receiving ink after blur while discarding its unfinished stroke', async () => {
    const shared = goodDocument(), server = inkServer(shared), ui = canvasFixture(undefined, { api: server.api });
    await ui.runtime.DeskPen.syncNow(); await ui.runtime.DeskPen.setFullscreen(true);
    ui.dispatch('pointerdown'); ui.dispatch('pointermove', 'pen', 1, 160, 180);
    await ui.emitWindow('blur');
    assert.equal(ui.runtime.DeskPen.isFullscreen(), false); assert.equal(ui.document.hidden, false);
    assert.equal(ui.saved().strokes.length, shared.strokes.length); assert.equal(ui.document.getElementById('pen-canvas').captures.size, 0);
    server.change({ ...shared, notes: 'New tablet note while the PC is unfocused' }); await ui.runtime.DeskPen.syncNow();
    assert.equal(notesIn(ui), 'New tablet note while the PC is unfocused');
    assert.equal(ui.saved().strokes.length, shared.strokes.length); assert.equal(writes(server).length, 0);
  });
  await asyncCheck('a late save acknowledgment keeps edits made while the save was pending', async () => {
    const server = inkServer(inkDocument('')); let resolveWrite, outgoing;
    const ui = canvasFixture(inkDocument('First local edit'), { api: async (route, body) => {
      if (body === undefined) return server.api(route, body);
      outgoing = plain(body); return new Promise(resolve => { resolveWrite = async () => resolve(await server.api(route, outgoing)); });
    } });
    const pending = ui.runtime.DeskPen.syncNow(); await waitUntil(() => !!resolveWrite);
    editNotes(ui, 'Newer edit while saving'); await resolveWrite(); await pending;
    assert.equal(outgoing.document.notes, 'First local edit'); assert.equal(server.snapshot().document.notes, 'First local edit');
    assert.equal(notesIn(ui), 'Newer edit while saving'); assert.equal(ui.saved().notes, 'Newer edit while saving');
  });
  await asyncCheck('choosing the PC version preserves the previous local ink as recovery', async () => {
    const server = inkServer(inkDocument('PC choice')), local = inkDocument('Local recovery'), ui = canvasFixture(local, { api: server.api });
    await ui.runtime.DeskPen.syncNow(); await ui.runtime.DeskPen.chooseRemote();
    assert.equal(notesIn(ui), 'PC choice'); assert.equal(JSON.parse(ui.data.get('deskwing.pen.recovery.v1')).notes, 'Local recovery');
    assert.equal(writes(server).length, 0);
  });
  await asyncCheck('delayed PC-version confirmation pauses polling and applies the approved version with local recovery', async () => {
    let resolveConfirmation;
    const server = inkServer(inkDocument('Approved PC version'));
    const ui = canvasFixture(inkDocument('Local recovery'), { api: server.api, confirmAction: () => new Promise(resolve => { resolveConfirmation = resolve; }) });
    await ui.runtime.DeskPen.syncNow(); const readsBefore = server.calls.length;
    const pending = ui.runtime.DeskPen.chooseRemote(); assert.equal(typeof resolveConfirmation, 'function');
    assert.equal(ui.document.getElementById('pen-sync-now').disabled, true);
    await ui.runtime.DeskPen.syncNow(); assert.equal(server.calls.length, readsBefore);
    assert.equal(notesIn(ui), 'Local recovery');
    resolveConfirmation(true); await pending;
    assert.equal(notesIn(ui), 'Approved PC version'); assert.equal(writes(server).length, 0);
    assert.equal(JSON.parse(ui.data.get('deskwing.pen.recovery.v1')).notes, 'Local recovery');
  });
  await asyncCheck('choosing the local version uses compare-and-swap and preserves the previous PC ink', async () => {
    const server = inkServer(inkDocument('PC recovery')), ui = canvasFixture(inkDocument('Local choice'), { api: server.api });
    await ui.runtime.DeskPen.syncNow(); await ui.runtime.DeskPen.chooseLocal();
    assert.equal(writes(server).length, 1); assert.equal(writes(server)[0].body.expectedRevision, 'r1');
    assert.equal(server.snapshot().document.notes, 'Local choice'); assert.equal(JSON.parse(ui.data.get('deskwing.pen.recovery.v1')).notes, 'PC recovery');
  });
  await asyncCheck('a PC change during delayed local-version confirmation still fails compare-and-swap safely', async () => {
    let resolveConfirmation;
    const server = inkServer(inkDocument('PC version reviewed'));
    const ui = canvasFixture(inkDocument('Local choice'), { api: server.api, confirmAction: () => new Promise(resolve => { resolveConfirmation = resolve; }) });
    await ui.runtime.DeskPen.syncNow(); const readsBefore = server.calls.length;
    const pending = ui.runtime.DeskPen.chooseLocal(); assert.equal(typeof resolveConfirmation, 'function');
    server.change(inkDocument('Newer PC edit during confirmation'));
    await ui.runtime.DeskPen.syncNow(); assert.equal(server.calls.length, readsBefore);
    resolveConfirmation(true); await pending;
    assert.equal(writes(server).length, 1); assert.equal(writes(server)[0].body.expectedRevision, 'r1');
    assert.equal(server.snapshot().document.notes, 'Newer PC edit during confirmation'); assert.equal(notesIn(ui), 'Local choice');
    assert.equal(ui.document.getElementById('pen-sync-state').textContent, 'Two versions need your choice');
  });
  await asyncCheck('explicit local choice cannot overwrite a newer unseen PC revision', async () => {
    const server = inkServer(inkDocument('Old PC')), ui = canvasFixture(inkDocument('Local choice'), { api: server.api });
    await ui.runtime.DeskPen.syncNow(); server.change(inkDocument('Newer PC')); await ui.runtime.DeskPen.chooseLocal();
    assert.equal(writes(server)[0].body.expectedRevision, 'r1'); assert.equal(server.snapshot().document.notes, 'Newer PC'); assert.equal(notesIn(ui), 'Local choice');
    assert.equal(ui.document.getElementById('pen-sync-state').textContent, 'Two versions need your choice');
  });
  await asyncCheck('reopening a recovery copy requires another explicit choice before PC replacement', async () => {
    const server = inkServer(inkDocument('PC copy')), ui = canvasFixture(inkDocument('Local recovery'), { api: server.api });
    await ui.runtime.DeskPen.syncNow(); await ui.runtime.DeskPen.chooseRemote(); await ui.runtime.DeskPen.restoreRecovery(); await ui.runtime.DeskPen.syncNow();
    assert.equal(notesIn(ui), 'Local recovery'); assert.equal(writes(server).length, 0);
    assert.equal(JSON.parse(ui.data.get('deskwing.pen.recovery.v1')).notes, 'PC copy');
  });
  await asyncCheck('persisted sync fingerprints distinguish unchanged local content after restart', async () => {
    const server = inkServer(inkDocument('Shared first')), first = canvasFixture(undefined, { api: server.api });
    await first.runtime.DeskPen.syncNow(); const saved = first.saved(), metadata = first.data.get('deskwing.pen.sync.v1');
    server.change(inkDocument('New PC after restart'));
    const reopened = canvasFixture(saved, { api: server.api, storage: { 'deskwing.pen.sync.v1': metadata } }); await reopened.runtime.DeskPen.syncNow();
    assert.equal(notesIn(reopened), 'New PC after restart'); assert.equal(writes(server).length, 0);
  });
  await asyncCheck('repeat ink polls send the known content revision and reuse an unchanged PC document', async () => {
    const revision = 'a'.repeat(64), remote = inkDocument('Shared cached notes'), calls = [];
    const ui = canvasFixture(undefined, { api: async (route, body) => {
      calls.push({ route, body }); assert.equal(body, undefined);
      return calls.length === 1 ? { workspaceId: 'cache-pc', revision, document: remote } : { workspaceId: 'cache-pc', revision, unchanged: true };
    } });
    await ui.runtime.DeskPen.syncNow(); const before = ui.saved(); await ui.runtime.DeskPen.syncNow();
    assert.deepEqual(calls.map(call => call.route), ['/api/ink', '/api/ink?revision=' + revision]);
    assert.equal(notesIn(ui), 'Shared cached notes'); assert.deepEqual(ui.saved(), before);
    assert.equal(ui.document.getElementById('pen-sync-state').textContent, 'Shared with your PC');
  });
  await asyncCheck('an unchanged poll still saves newer local notes with the expected content revision', async () => {
    const revision = 'a'.repeat(64), savedRevision = 'b'.repeat(64), remote = inkDocument('Original shared notes'), calls = [];
    const ui = canvasFixture(undefined, { api: async (route, body) => {
      calls.push({ route, body: body === undefined ? undefined : plain(body) });
      if (body !== undefined) return { workspaceId: 'cache-pc', revision: savedRevision, document: body.document };
      return calls.length === 1 ? { workspaceId: 'cache-pc', revision, document: remote } : { workspaceId: 'cache-pc', revision, unchanged: true };
    } });
    await ui.runtime.DeskPen.syncNow(); editNotes(ui, 'New local note'); await ui.runtime.DeskPen.syncNow();
    assert.equal(calls[1].route, '/api/ink?revision=' + revision);
    assert.equal(calls[2].route, '/api/ink'); assert.equal(calls[2].body.expectedRevision, revision);
    assert.equal(calls[2].body.document.notes, 'New local note'); assert.equal(notesIn(ui), 'New local note');
    assert.equal(JSON.parse(ui.data.get('deskwing.pen.sync.v1')).revision, savedRevision);
  });
  await asyncCheck('unchanged replies with a mismatched PC identity or revision cannot replace local ink or trigger writes', async () => {
    const revision = 'a'.repeat(64);
    for (const mismatch of [{ workspaceId: 'different-pc', revision }, { workspaceId: 'cache-pc', revision: 'b'.repeat(64) }]) {
      const calls = [], remote = inkDocument('Original shared notes');
      const ui = canvasFixture(undefined, { api: async (route, body) => {
        calls.push({ route, body }); assert.equal(body, undefined);
        return calls.length === 1 ? { workspaceId: 'cache-pc', revision, document: remote } : { ...mismatch, unchanged: true };
      } });
      await ui.runtime.DeskPen.syncNow(); editNotes(ui, 'Keep local edit'); await ui.runtime.DeskPen.syncNow();
      assert.equal(calls.length, 2); assert.equal(notesIn(ui), 'Keep local edit'); assert.equal(ui.saved().notes, 'Keep local edit');
      assert.equal(ui.document.getElementById('pen-sync-state').textContent, 'Offline · local copy kept');
    }
  });
}
async function fullscreenChecks() {
  check('fullscreen paper fitting preserves the drawing aspect ratio in wide and tall spaces', () => {
    for (const [width, height] of [[1600, 900], [600, 1000], [800, 500]]) {
      const fitted = core.fitPaper(width, height);
      assert.ok(fitted.width <= width && fitted.height <= height); assert.equal(fitted.width / fitted.height, 1.6);
      const p = core.makePoint({ clientX: fitted.width / 4, clientY: fitted.height / 2, pressure: 0.7 }, { left: 0, top: 0, ...fitted });
      assert.deepEqual(plain(p), [0.25, 0.5, 0.7]);
    }
  });
  await asyncCheck('fullscreen can show notes and Escape keeps committed ink and notes', async () => {
    const seed = goodDocument(), ui = canvasFixture(seed);
    await ui.runtime.DeskPen.setFullscreen(true); assert.equal(ui.runtime.DeskPen.isFullscreen(), true);
    assert.equal(ui.document.querySelector('.pen-notes').hidden, true);
    await ui.document.getElementById('pen-fullscreen-notes').click(); assert.equal(ui.document.querySelector('.pen-notes').hidden, false);
    ui.dispatch('pointerdown'); ui.dispatch('pointermove', 'pen', 1, 100, 120);
    await ui.emitDocument('keydown', { key: 'Escape' });
    assert.equal(ui.runtime.DeskPen.isFullscreen(), false); assert.deepEqual(ui.saved().strokes, seed.strokes); assert.equal(ui.saved().notes, seed.notes);
  });
  await asyncCheck('browser fullscreen exit clears app fullscreen and drops partial ink', async () => {
    const ui = canvasFixture(goodDocument()); await ui.runtime.DeskPen.setFullscreen(true);
    ui.dispatch('pointerdown'); await ui.document.exitFullscreen();
    assert.equal(ui.runtime.DeskPen.isFullscreen(), false); assert.equal(ui.saved().strokes.length, 1);
    assert.equal(ui.document.body.classList.contains('pen-fullscreen'), false);
  });
  await asyncCheck('suspension exits fullscreen and preserves the committed note', async () => {
    const ui = canvasFixture(inkDocument('Keep this note')); await ui.runtime.DeskPen.setFullscreen(true);
    ui.dispatch('pointerdown'); ui.runtime.DeskPen.suspend(); await Promise.resolve();
    assert.equal(ui.runtime.DeskPen.isFullscreen(), false); assert.equal(ui.saved().notes, 'Keep this note'); assert.equal(ui.saved().strokes.length, 0);
  });
  await asyncCheck('in-app fullscreen remains usable when browser fullscreen is unavailable', async () => {
    const ui = canvasFixture(undefined, { fullscreenRejected: true });
    assert.equal(await ui.runtime.DeskPen.setFullscreen(true), true); assert.equal(ui.runtime.DeskPen.isFullscreen(), true);
    assert.equal(ui.document.body.classList.contains('pen-fullscreen'), true);
    await ui.runtime.DeskPen.setFullscreen(false); assert.equal(ui.runtime.DeskPen.isFullscreen(), false);
  });
  await asyncCheck('leaving the pen workspace exits fullscreen without changing normalized ink', async () => {
    const seed = goodDocument(), ui = canvasFixture(seed); await ui.runtime.DeskPen.setFullscreen(true);
    ui.runtime.DeskPen.setActive(false); await Promise.resolve();
    assert.equal(ui.runtime.DeskPen.isFullscreen(), false); assert.equal(ui.runtime.DeskPen.isActive(), false);
    assert.deepEqual(ui.saved().strokes, seed.strokes);
  });
}
async function handwritingChecks() {
  await asyncCheck('desktop handwriting entry offers a text fallback without invoking native code', async () => {
    const field = textField(); let called = false;
    context.native = false; context.nativeRequest = async () => { called = true; };
    assert.equal(await context.DeskPen.requestHandwriting(field), false);
    assert.equal(called, false); assert.equal(field.focused, true); assert.equal(field.value, 'Original');
  });
  context.native = true;
  await asyncCheck('native cancellation leaves text untouched', async () => {
    const field = textField(); context.nativeRequest = async () => ({ text: 'Ignored', cancelled: true });
    assert.equal(await context.DeskPen.requestHandwriting(field), false);
    assert.equal(field.value, 'Original'); assert.deepEqual(field.events, []);
  });
  await asyncCheck('handwriting uses the local-only native contract and emits normal input events', async () => {
    const field = textField(); let call;
    context.nativeRequest = async (...args) => { call = args; return { text: 'New note', cancelled: false }; };
    assert.equal(await context.DeskPen.requestHandwriting(field), true);
    assert.deepEqual(plain(call), ['/local/handwriting', { text: 'Original' }, 'POST', 0]);
    assert.equal(field.value, 'New note'); assert.deepEqual(field.events, ['input', 'change']);
  });
  await asyncCheck('handwriting never truncates or overwrites a field with a smaller limit', async () => {
    const field = textField('Old', 5); context.nativeRequest = async () => ({ text: 'Too long', cancelled: false });
    assert.equal(await context.DeskPen.requestHandwriting(field), false); assert.equal(field.value, 'Old');
  });
  await asyncCheck('newer text wins over a late handwriting result', async () => {
    const field = textField(); let resolve;
    context.nativeRequest = () => new Promise(done => { resolve = done; });
    const pending = context.DeskPen.requestHandwriting(field);
    field.value = 'Newer edit'; resolve({ text: 'Older handwriting', cancelled: false });
    assert.equal(await pending, false); assert.equal(field.value, 'Newer edit');
  });
  await asyncCheck('only one native handwriting request can be active', async () => {
    const first = textField(), second = textField(); let resolve, calls = 0;
    context.nativeRequest = () => { calls++; return new Promise(done => { resolve = done; }); };
    const pending = context.DeskPen.requestHandwriting(first);
    assert.equal(await context.DeskPen.requestHandwriting(second), false); assert.equal(calls, 1);
    resolve({ text: 'Finished', cancelled: false }); assert.equal(await pending, true);
  });
  await asyncCheck('removed fields cannot receive late handwriting text', async () => {
    const field = textField(); let resolve;
    context.nativeRequest = () => new Promise(done => { resolve = done; });
    const pending = context.DeskPen.requestHandwriting(field);
    field.isConnected = false; resolve({ text: 'Late', cancelled: false });
    assert.equal(await pending, false); assert.equal(field.value, 'Original');
  });
  await asyncCheck('password fields cannot be submitted to the handwriting bridge', async () => {
    const field = { ...textField('secret'), tagName: 'INPUT', type: 'password' }; let called = false;
    context.nativeRequest = async () => { called = true; return { text: 'New', cancelled: false }; };
    assert.equal(await context.DeskPen.requestHandwriting(field), false); assert.equal(called, false);
  });
  await canvasChecks();
  await syncChecks(); await fullscreenChecks();
  console.log(`${passed} pen state, pointer, history, canvas, handwriting, shared-ink and fullscreen checks passed. No physical stylus/IME validation claimed.`);
}
handwritingChecks().catch(error => { console.error(error); process.exitCode = 1; });
