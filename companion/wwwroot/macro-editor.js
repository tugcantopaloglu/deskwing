'use strict';

// A macro editor only edits configuration and asks for read-only target checks.
// It never executes a step, launches a selected file, or changes OBS state.
(function () {
  const mounts = new WeakMap();
  const activeReads = new Set();
  const maxSteps = 30;
  const maxDepth = 3;
  let serial = 0;
  const defaultKinds = {
    scene: 'Switch OBS scene', preview: 'Set preview scene', transition: 'Take studio transition',
    sound: 'Play sound on PC', stopSounds: 'Stop all deck sounds', app: 'Open Windows app',
    url: 'Open website', hotkey: 'Send keyboard shortcut', mute: 'Toggle OBS input mute',
    muteOn: 'Mute OBS input', muteOff: 'Unmute OBS input', source: 'Toggle source visibility',
    filter: 'Toggle source filter', media: 'Restart / stop OBS media', record: 'Toggle recording',
    recordStart: 'Start recording', recordStop: 'Stop recording', stream: 'Toggle streaming',
    streamStart: 'Start streaming', streamStop: 'Stop streaming', replay: 'Save replay buffer',
    virtualCamera: 'Toggle virtual camera', studioMode: 'Toggle studio mode',
    soundpadPlay: 'Soundpad · play sound', soundpadStop: 'Soundpad · stop',
    soundpadPause: 'Soundpad · pause / resume', soundpadNext: 'Soundpad · next sound',
    soundpadPrevious: 'Soundpad · previous sound', voicemeeter: 'Voicemeeter · set gain or mute',
    macro: 'Action group', delay: 'Delay'
  };
  const groups = [
    ['OBS scenes and sources', ['scene', 'preview', 'transition', 'source', 'filter', 'media']],
    ['Audio and sounds', ['sound', 'stopSounds', 'mute', 'muteOn', 'muteOff', 'soundpadPlay', 'soundpadStop', 'soundpadPause', 'soundpadNext', 'soundpadPrevious', 'voicemeeter']],
    ['Broadcast', ['recordStart', 'recordStop', 'record', 'streamStart', 'streamStop', 'stream', 'replay', 'virtualCamera', 'studioMode']],
    ['Windows', ['app', 'url', 'hotkey']], ['Timing and groups', ['delay', 'macro']]
  ];
  const forcedConfirm = new Set(['stream', 'streamStart', 'streamStop', 'record', 'recordStart', 'recordStop']);
  const obsKinds = new Set(['scene', 'preview', 'source', 'filter', 'media', 'mute', 'muteOn', 'muteOff']);
  const inputKinds = new Set(['media', 'mute', 'muteOn', 'muteOff']);
  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }
  function button(text, action, className = 'quiet') {
    const node = element('button', className, text);
    node.type = 'button';
    node.onclick = action;
    return node;
  }
  function option(value, text) {
    const node = element('option', '', text);
    node.value = value;
    return node;
  }
  function summary(action) {
    const result = { actions: 0, delaysMs: 0, invalidDelays: 0 };
    const visit = (step, depth) => {
      if (!step || depth > maxDepth + 1) return;
      if (step.kind === 'macro') {
        for (const child of step.steps || []) visit(child, depth + 1);
      } else if (step.kind === 'delay') {
        const value = String(step.target ?? '').trim();
        if (!/^\d+$/.test(value) || Number(value) > 10000) result.invalidDelays++;
        else result.delaysMs += Number(value);
      } else result.actions++;
    };
    visit(action, 0);
    return result;
  }
  function cloneStep(step) {
    const copy = structuredClone(step);
    const renew = node => {
      node.id = crypto.randomUUID().replaceAll('-', '');
      for (const child of node.steps || []) renew(child);
    };
    renew(copy);
    return copy;
  }
  function makeStep(kind, labels) {
    return {
      id: crypto.randomUUID().replaceAll('-', ''), label: labels[kind] || 'Macro step', kind,
      target: kind === 'delay' ? '500' : '', value: '', icon: kind === 'delay' ? 'clock' : 'bolt',
      color: 'violet', confirm: false, soundpadKey: '', steps: []
    };
  }
  function names(value, key) {
    if (!Array.isArray(value)) return [];
    return [...new Set(value.map(item => typeof item === 'string' ? item : item?.[key]).filter(item => typeof item === 'string' && item.length > 0))];
  }
  function mount(container, action, options = {}) {
    if (!container || !action || action.kind !== 'macro') return;
    action.steps ??= [];
    const labels = { ...defaultKinds, ...(options.kinds || {}) };
    labels.macro = 'Action group';
    const session = { id: ++serial, drag: null, lists: [], summaryNode: null, status: null };
    mounts.set(container, session);
    const alive = () => container.isConnected && mounts.get(container) === session;
    const changed = () => {
      if (!alive()) return;
      for (const status of container.querySelectorAll('.macro-step-status')) {
        if (status.textContent && status.textContent !== 'Checking…') status.textContent = 'Values changed. Check again when ready.';
      }
      options.changed?.(); updateSummary();
    };
    const tell = message => { if (alive() && session.status) session.status.textContent = message; };
    const request = (path, body, timeout) => {
      if (typeof options.api !== 'function') return Promise.reject(new Error('Open the Windows companion to discover targets.'));
      return options.api(path, body, timeout);
    };
    function updateSummary() {
      const value = summary(action);
      if (session.summaryNode) session.summaryNode.textContent = `${value.actions} ${value.actions === 1 ? 'action' : 'actions'} · ${(value.delaysMs / 1000).toLocaleString(undefined, { maximumFractionDigits: 3 })} s planned delays + action time${value.invalidDelays ? ' · Fix the highlighted delays' : ''}`;
    }
    function structureChange(edit, focusPath, field = 'kind') {
      if (!alive()) return;
      edit();
      changed();
      if (!alive()) return;
      render();
      if (focusPath !== undefined) container.querySelector(`[data-step="${focusPath}"][data-field="${field}"]`)?.focus();
    }
    function field(host, label, step, key, path, settings = {}) {
      const wrap = element('label', settings.wide ? 'wide' : '');
      wrap.append(element('span', '', label));
      const input = element(settings.choices ? 'select' : 'input');
      input.dataset.step = path;
      input.dataset.field = key;
      input.setAttribute('aria-label', `Step ${path.split('.').map(n => Number(n) + 1).join('.')}: ${label}`);
      if (settings.choices) {
        for (const [value, text] of settings.choices) input.append(option(value, text));
        if (!settings.choices.some(([value]) => value === String(step[key] ?? '')) && step[key]) input.append(option(String(step[key]), `Saved value: ${step[key]}`));
      } else {
        input.type = settings.type || 'text';
        input.maxLength = key === 'label' ? 60 : key === 'value' ? 1024 : 2048;
        if (settings.placeholder) input.placeholder = settings.placeholder;
        if (settings.min !== undefined) input.min = String(settings.min);
        if (settings.max !== undefined) input.max = String(settings.max);
        if (settings.step !== undefined) input.step = String(settings.step);
        input.autocomplete = 'off';
        if (settings.type === 'number') input.inputMode = 'numeric';
      }
      input.value = String(step[key] ?? '');
      input.oninput = () => {
        if (!alive() || !input.isConnected) return;
        step[key] = input.value;
        if (step.kind === 'soundpadPlay' && key === 'target') step.soundpadKey = '';
        settings.update?.(input);
        changed();
      };
      wrap.append(input);
      host.append(wrap);
      return input;
    }
    function suggestions(input, values, suffix) {
      if (!input) return;
      const id = `macro-options-${session.id}-${suffix}`;
      let list = container.querySelector(`#${id}`);
      if (!list) { list = element('datalist'); list.id = id; container.append(list); }
      list.replaceChildren(...values.map(value => option(value, value)));
      input.setAttribute('list', id);
    }
    function renderStep(list, step, index, depth, prefix, parent) {
      const path = prefix ? `${prefix}.${index}` : String(index);
      const display = path.split('.').map(n => Number(n) + 1).join('.');
      const row = element('fieldset', 'step macro-step');
      row.dataset.macroStep = path;
      const legend = element('legend', 'macro-step-title', `Step ${display}`);
      row.append(legend);
      const head = element('div', 'macro-step-head');
      const caption = element('strong', '', step.label || labels[step.kind] || step.kind);
      head.append(caption);
      const controls = element('div', 'macro-step-controls');
      const move = offset => {
        const at = list.indexOf(step), next = at + offset;
        if (at < 0 || next < 0 || next >= list.length) return;
        structureChange(() => { list.splice(at, 1); list.splice(next, 0, step); }, prefix ? `${prefix}.${next}` : String(next));
      };
      const up = button('Earlier', () => move(-1));
      const down = button('Later', () => move(1));
      up.disabled = index === 0; down.disabled = index === list.length - 1;
      up.setAttribute('aria-label', `Move step ${display} earlier`);
      down.setAttribute('aria-label', `Move step ${display} later`);
      const duplicate = button('Duplicate', () => {
        if (list.length >= maxSteps) return;
        structureChange(() => list.splice(list.indexOf(step) + 1, 0, cloneStep(step)), prefix ? `${prefix}.${index + 1}` : String(index + 1), 'label');
      });
      duplicate.disabled = list.length >= maxSteps;
      duplicate.setAttribute('aria-label', `Duplicate step ${display}`);
      const remove = button('Remove', () => {
        const at = list.indexOf(step);
        if (at < 0) return;
        const next = Math.min(at, list.length - 2);
        structureChange(() => list.splice(at, 1), next >= 0 ? prefix ? `${prefix}.${next}` : String(next) : undefined);
        if (next < 0 && alive()) options.addButton?.focus();
      }, 'quiet macro-remove');
      remove.setAttribute('aria-label', `Remove step ${display}`);
      const drag = button('Drag', () => tell('Drag a step within its group, or use Earlier and Later.'));
      drag.className = 'quiet macro-drag'; drag.draggable = true;
      drag.setAttribute('aria-label', `Drag step ${display} to reorder`);
      drag.ondragstart = event => {
        session.drag = { list, step };
        event.dataTransfer?.setData('text/plain', `macro-${session.id}`);
        if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
      };
      drag.ondragend = () => { session.drag = null; row.classList.remove('macro-drop-target'); };
      row.ondragover = event => {
        if (session.drag) event.stopPropagation();
        if (session.drag?.list === list && session.drag.step !== step) { event.preventDefault(); row.classList.add('macro-drop-target'); }
      };
      row.ondragleave = () => row.classList.remove('macro-drop-target');
      row.ondrop = event => {
        event.stopPropagation();
        if (session.drag?.list !== list || session.drag.step === step) return;
        event.preventDefault();
        const moving = session.drag.step, from = list.indexOf(moving), to = list.indexOf(step);
        session.drag = null;
        if (from >= 0 && to >= 0) structureChange(() => { list.splice(from, 1); list.splice(to, 0, moving); }, path);
      };
      controls.append(up, down, duplicate, remove, drag); head.append(controls); row.append(head);
      const form = element('div', 'form-grid macro-step-fields');
      const name = field(form, 'Step name', step, 'label', path, { update: input => { caption.textContent = input.value || labels[step.kind]; } });
      name.required = true;
      const actionWrap = element('label'); actionWrap.append(element('span', '', 'Action'));
      const kind = element('select'); kind.dataset.step = path; kind.dataset.field = 'kind';
      kind.setAttribute('aria-label', `Step ${display}: Action`);
      const offered = new Set();
      for (const [title, keys] of groups) {
        const group = element('optgroup'); group.label = title;
        for (const key of keys) {
          if (key === 'macro' && depth >= maxDepth) continue;
          if (labels[key]) { group.append(option(key, labels[key])); offered.add(key); }
        }
        kind.append(group);
      }
      if (!offered.has(step.kind)) kind.append(option(step.kind, `${labels[step.kind] || step.kind} — replace this unsupported step`));
      kind.value = step.kind;
      kind.onchange = () => {
        const oldLabel = labels[step.kind];
        structureChange(() => {
          if (!step.label || step.label === oldLabel || step.label === 'Macro step') step.label = labels[kind.value];
          step.kind = kind.value; step.target = kind.value === 'delay' ? '500' : ''; step.value = '';
          step.soundpadKey = ''; step.steps = []; step.confirm = false;
        }, path);
      };
      actionWrap.append(kind); form.append(actionWrap);
      let target, value;
      if (['scene', 'preview', 'source'].includes(step.kind)) target = field(form, 'OBS scene', step, 'target', path, { wide: true, placeholder: 'Choose a discovered scene or enter its exact name' });
      if (inputKinds.has(step.kind)) target = field(form, 'OBS input', step, 'target', path, { wide: true, placeholder: 'Exact input name in OBS' });
      if (step.kind === 'source') value = field(form, 'Source in this scene', step, 'value', path, { wide: true, placeholder: 'Exact scene source name' });
      if (step.kind === 'filter') {
        target = field(form, 'OBS source', step, 'target', path, { wide: true, placeholder: 'Input or scene containing the filter' });
        value = field(form, 'Filter on this source', step, 'value', path, { wide: true, placeholder: 'Exact filter name' });
      }
      if (step.kind === 'media') value = field(form, 'Media operation', step, 'value', path, { choices: [['', 'Restart (default)'], ['restart', 'Restart'], ['stop', 'Stop']] });
      if (['sound', 'app'].includes(step.kind)) target = field(form, step.kind === 'sound' ? 'Sound file on PC' : 'App or shortcut on PC', step, 'target', path, { wide: true, placeholder: step.kind === 'sound' ? 'C:\\Sounds\\applause.wav' : 'C:\\Apps\\App.exe or an absolute .lnk path' });
      if (step.kind === 'url') target = field(form, 'Website address', step, 'target', path, { wide: true, type: 'url', placeholder: 'https://example.com' });
      if (step.kind === 'hotkey') target = field(form, 'Keyboard shortcut', step, 'target', path, { wide: true, placeholder: 'CTRL+SHIFT+M' });
      if (step.kind === 'delay') {
        target = field(form, 'Delay in milliseconds', step, 'target', path, { type: 'number', min: 0, max: 10000, step: 1, update: input => validateDelay(input) });
        const hint = element('p', 'help macro-field-help', '0–10,000 ms per step. 1,000 ms = 1 second.'); form.append(hint);
        function validateDelay(input) { const valid = /^\d+$/.test(input.value) && Number(input.value) <= 10000; input.setAttribute('aria-invalid', String(!valid)); input.setCustomValidity(valid ? '' : 'Enter a whole number from 0 to 10,000 milliseconds.'); }
        validateDelay(target);
      }
      if (step.kind === 'soundpadPlay') {
        target = field(form, 'Soundpad All sounds index', step, 'target', path, { type: 'number', min: 1, step: 1 });
        value = field(form, 'Play through', step, 'value', path, { choices: [['', 'Soundpad default'], ['default', 'Soundpad default (explicit)'], ['both', 'Microphone and speakers'], ['mic', 'Microphone'], ['speakers', 'Speakers']] });
      }
      if (step.kind === 'voicemeeter') {
        target = field(form, 'Voicemeeter parameter', step, 'target', path, { placeholder: 'Strip[0].Gain or Bus[0].Mute', wide: true });
        value = field(form, 'Gain in dB, or mute 0 / 1', step, 'value', path, { placeholder: '-6 for gain; 1 to mute; 0 to unmute', wide: true });
      }
      if (target) target.required = true;
      if (value && ['source', 'filter', 'voicemeeter'].includes(step.kind)) value.required = true;
      row.append(form);
      if (step.kind === 'holdHotkey') row.append(element('p', 'macro-error', 'Hold shortcuts cannot run inside a macro. Choose a tap shortcut or remove this step.'));
      if (step.kind === 'hotkey') row.append(element('p', 'help', 'Uses the foreground app or an app’s configured global shortcut. Check the intended app before running.'));
      if (step.kind === 'sound') row.append(element('p', 'help', 'Plays on the PC’s output. Include that output in your stream audio if viewers should hear it.'));
      if (forcedConfirm.has(step.kind)) row.append(element('p', 'help', 'This broadcast action always requires confirmation before the macro runs.'));
      else if (step.kind !== 'holdHotkey') {
        const confirm = element('label', 'check macro-confirm');
        const check = element('input'); check.type = 'checkbox'; check.checked = !!step.confirm;
        check.onchange = () => { step.confirm = check.checked; changed(); };
        confirm.append(check, element('span', '', 'Require confirmation before this macro runs')); row.append(confirm);
      }
      const tools = element('div', 'row macro-step-tools');
      const state = element('p', 'help macro-step-status'); state.setAttribute('role', 'status');
      const stillHere = () => alive() && row.isConnected && list.includes(step);
      async function read(buttonNode, work) {
        if (!stillHere()) return;
        const pending = { container, session };
        activeReads.add(pending);
        buttonNode.disabled = true; state.textContent = 'Checking…';
        try { await work(); } catch (error) { if (stillHere()) state.textContent = error.message + ' Your target is unchanged.'; }
        finally { activeReads.delete(pending); if (stillHere()) buttonNode.disabled = false; }
      }
      if (obsKinds.has(step.kind)) {
        const discover = button('Find OBS names', () => read(discover, async () => {
          const requested = { kind: step.kind, target: step.target };
          const query = new URLSearchParams();
          if (step.kind === 'source' && step.target) query.set('scene', step.target);
          if (step.kind === 'filter' && step.target) query.set('source', step.target);
          const result = await request('/api/admin/obs/targets' + (query.size ? '?' + query.toString() : ''));
          if (!stillHere()) return;
          if (requested.kind !== step.kind || requested.target !== step.target) { state.textContent = 'Target changed. Find OBS names again for the current target.'; return; }
          const scenes = names(result.scenes, 'sceneName'), inputs = names(result.inputs, 'inputName');
          const sources = names(result.sources, 'sourceName'), filters = names(result.filters, 'filterName');
          const targets = inputKinds.has(step.kind) ? inputs : step.kind === 'filter' ? [...new Set([...inputs, ...scenes])] : scenes;
          suggestions(target, targets, path.replaceAll('.', '-') + '-target');
          if (value) suggestions(value, step.kind === 'source' ? sources : filters, path.replaceAll('.', '-') + '-value');
          state.textContent = step.kind === 'source' || step.kind === 'filter'
            ? 'Names loaded. Choose the scene/source first, then Find OBS names again for its contents.'
            : targets.length ? 'Names loaded. Choose a suggestion or keep your exact name.' : 'No names are available. Check OBS setup or enter the exact name.';
        })); tools.append(discover);
      }
      if (step.kind === 'sound' || step.kind === 'app') {
        const pick = button(step.kind === 'sound' ? 'Choose sound file' : 'Choose app file', () => read(pick, async () => {
          const before = { kind: step.kind, target: step.target };
          const result = await request('/api/admin/pick-file', { kind: step.kind }, 0);
          if (!stillHere()) return;
          if (before.kind !== step.kind || before.target !== step.target) { state.textContent = 'Target changed while the chooser was open. Kept your typed value.'; return; }
          if (result.path) { step.target = result.path; target.value = result.path; changed(); state.textContent = 'File selected. It has not been opened or played.'; }
          else state.textContent = 'File selection cancelled.';
        })); tools.append(pick);
      }
      const check = button('Check step', () => read(check, async () => {
        const invalid = [...form.querySelectorAll('input, select')].find(control => !control.reportValidity());
        if (invalid) { state.textContent = 'Fix the highlighted field, then check this step again.'; return; }
        const before = JSON.stringify(step);
        const result = await request('/api/admin/action-check', { action: structuredClone(step) });
        if (!stillHere()) return;
        if (before !== JSON.stringify(step)) { state.textContent = 'Step changed. Check the current values again.'; return; }
        state.textContent = (result.ok ? 'Target checks passed. ' : (result.problems || []).join(' ') + ' ') + (result.note || 'Validation only; no action ran.');
      })); tools.append(check); row.append(tools, state);
      if (step.kind === 'macro') {
        step.steps ??= [];
        if (depth >= maxDepth) row.append(element('p', 'macro-error', 'This action group is too deeply nested. Move its actions into a higher group or remove it.'));
        else {
          const nested = element('details', 'macro-nested'); nested.open = true;
          nested.append(element('summary', '', `Actions in step ${display} (${step.steps.length})`));
          renderList(nested, step.steps, depth + 1, path); row.append(nested);
        }
      }
      parent.append(row);
    }
    function renderList(host, list, depth, prefix) {
      session.lists.push(list);
      if (!list.length) host.append(element('p', 'help macro-empty', 'No actions yet. Add an action or a timed delay.'));
      list.forEach((step, index) => renderStep(list, step, index, depth, prefix, host));
      if (list.length > maxSteps) host.append(element('p', 'macro-error', 'Keep at most 30 steps in each group. Remove extra steps before saving.'));
      const controls = element('div', 'row macro-add-actions');
      const add = kind => { if (list.length >= maxSteps) return; const path = prefix ? `${prefix}.${list.length}` : String(list.length); structureChange(() => list.push(makeStep(kind, labels)), path, kind === 'delay' ? 'target' : 'kind'); };
      if (prefix || !options.addButton) {
        const addAction = button('Add action', () => add('scene'));
        addAction.disabled = list.length >= maxSteps; controls.append(addAction);
      } else {
        options.addButton.textContent = 'Add action'; options.addButton.disabled = list.length >= maxSteps;
        options.addButton.onclick = () => add('scene');
      }
      const delay = button('Add delay', () => add('delay')); delay.disabled = list.length >= maxSteps;
      controls.append(delay, element('span', 'help', `${list.length} / ${maxSteps} steps`)); host.append(controls);
    }
    function render() {
      if (!alive()) return;
      container.replaceChildren(); container.classList.add('macro-composer'); session.lists = [];
      session.summaryNode = element('p', 'macro-timing'); session.summaryNode.setAttribute('role', 'status');
      container.append(session.summaryNode);
      container.append(element('p', 'help macro-help', 'Groups can nest up to three step levels. All required confirmations happen before execution; a failed step stops the macro.'));
      renderList(container, action.steps, 1, '');
      session.status = element('p', 'help macro-status'); session.status.setAttribute('role', 'status'); container.append(session.status);
      updateSummary();
    }
    render();
    return { render, summary: () => summary(action) };
  }
  window.DeckMacroEditor = Object.freeze({
    mount, summary, cloneStep,
    hasPending: () => [...activeReads].some(read => read.container.isConnected && mounts.get(read.container) === read.session)
  });
})();

function renderMacroEditor(action) {
  return window.DeckMacroEditor.mount(document.querySelector('#macro-steps'), action, {
    changed: () => markDirty(), api: (path, body, timeout) => api(path, body, timeout),
    kinds, addButton: document.querySelector('#add-step')
  });
}
