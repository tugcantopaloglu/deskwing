'use strict';
// The native PC window uses the same authenticated document and shared-ink lifecycle.
(function () {
  if (typeof desktopConsole === 'undefined' || !desktopConsole) return;
  const dedicated = new URLSearchParams(location.search).get('workspace') === 'ink-window';
  let opening = false, observer = null, retryPanel = null, compactObserver = null;
  let toolsOpen = false, notesOpen = false;
  if (dedicated) {
    document.body.classList.add('ink-window-mode');
    document.title = 'DeskWing Ink';
  }
  async function openWindow(button) {
    if (opening) return;
    opening = true; button.disabled = true;
    try {
      await api('/api/admin/ink/window', {});
      if (typeof notice === 'function') notice('Ink window opened. Resize it independently of the companion.');
    } catch (error) {
      if (typeof notice === 'function') notice(error?.message || 'The ink window could not open. Try again.', true);
    } finally { opening = false; button.disabled = false; }
  }
  function addOpenButton() {
    const actions = document.querySelector('#pen-workspace .pen-heading-actions');
    if (!actions) return false;
    if (!document.getElementById('pen-open-window')) {
      const button = document.createElement('button');
      button.id = 'pen-open-window'; button.type = 'button'; button.className = 'quiet'; button.textContent = 'Open ink window';
      button.addEventListener('click', () => openWindow(button));
      actions.append(button);
    }
    observer?.disconnect(); observer = null;
    return true;
  }
  function resizeInk() { requestAnimationFrame(() => window.DeskPen?.resize?.()); }
  function prepareCompactWindow() {
    const host = document.getElementById('pen-workspace'), actions = host?.querySelector('.pen-heading-actions');
    const box = document.getElementById('pen-toolbox'), notes = host?.querySelector('.pen-notes');
    if (!host || !actions || !box || !notes || document.getElementById('ink-window-tools')) return;
    const tools = document.createElement('button'); tools.type = 'button'; tools.id = 'ink-window-tools'; tools.className = 'quiet'; tools.setAttribute('aria-controls', 'pen-toolbox');
    const toggleNotes = document.createElement('button'); toggleNotes.type = 'button'; toggleNotes.id = 'ink-window-notes'; toggleNotes.className = 'quiet ink-window-only';
    const closeNotes = document.createElement('button'); closeNotes.type = 'button'; closeNotes.id = 'ink-window-hide-notes'; closeNotes.className = 'quiet ink-window-only'; closeNotes.textContent = 'Hide notes';
    function update() {
      host.classList.toggle('ink-window-tools-open', toolsOpen); host.classList.toggle('ink-window-notes-open', notesOpen);
      tools.textContent = toolsOpen ? 'Hide tools' : 'Show tools'; tools.setAttribute('aria-expanded', String(toolsOpen));
      toggleNotes.textContent = notesOpen ? 'Hide notes' : 'Show notes'; toggleNotes.setAttribute('aria-expanded', String(notesOpen));
      showWarning(); resizeInk();
    }
    tools.addEventListener('click', () => { toolsOpen = !toolsOpen; if (toolsOpen) notesOpen = false; update(); });
    toggleNotes.addEventListener('click', () => { notesOpen = !notesOpen; toolsOpen = false; update(); if (notesOpen) document.getElementById('pen-notes')?.focus(); });
    closeNotes.addEventListener('click', () => { notesOpen = false; update(); tools.focus(); });
    actions.append(tools); (box.querySelector('.pen-drawer-actions') || box).append(toggleNotes); (notes.querySelector('.pen-notes-heading') || notes).append(closeNotes);
    document.getElementById('pen-canvas')?.addEventListener('pointerdown', () => { if (toolsOpen || notesOpen) { toolsOpen = false; notesOpen = false; update(); } });
    document.addEventListener('keydown', event => {
      if (event.key === 'Escape' && !host.classList.contains('is-fullscreen') && (toolsOpen || notesOpen)) {
        event.preventDefault(); toolsOpen = false; notesOpen = false; update(); tools.focus();
      }
    });
    function showWarning() {
      const conflict = host.querySelector('.pen-conflict'), status = document.getElementById('pen-status'), sync = document.getElementById('pen-sync-state');
      const warning = !!(conflict && !conflict.hidden || status?.classList.contains('pen-error') || /Offline|Two versions/.test(sync?.textContent || ''));
      tools.classList.toggle('ink-window-warning', warning); tools.setAttribute('aria-label', (warning ? 'Ink needs attention. ' : '') + tools.textContent);
    }
    if (typeof MutationObserver !== 'undefined') { compactObserver = new MutationObserver(showWarning); compactObserver.observe(box, {childList:true, subtree:true, characterData:true, attributes:true, attributeFilter:['hidden','class']}); }
    update(); showWarning();
  }
  async function activateInk() {
    const retry = retryPanel?.querySelector('button');
    if (retry) retry.disabled = true;
    try {
      if (typeof setWorkspaceMode !== 'function' || !await setWorkspaceMode('pen')) throw new Error('Finish the current action, then try opening ink again.');
      retryPanel?.remove(); retryPanel = null;
      prepareCompactWindow();
    } catch (error) {
      if (!retryPanel) {
        retryPanel = document.createElement('section'); retryPanel.className = 'ink-window-error'; retryPanel.setAttribute('role', 'status');
        const title = document.createElement('h1'); title.textContent = 'Ink & notes';
        const message = document.createElement('p');
        const button = document.createElement('button'); button.type = 'button'; button.className = 'quiet'; button.textContent = 'Open ink'; button.addEventListener('click', activateInk);
        retryPanel.append(title, message, button); document.body.append(retryPanel);
      }
      retryPanel.querySelector('p').textContent = error?.message || 'The ink view could not open. Try again.';
      retryPanel.querySelector('button').disabled = false;
    }
  }
  function initialize() {
    if (dedicated) { void activateInk(); return; }
    if (addOpenButton()) return;
    const host = document.getElementById('pen-workspace');
    if (host && typeof MutationObserver !== 'undefined') {
      observer = new MutationObserver(addOpenButton);
      observer.observe(host, {childList:true, subtree:true});
    }
  }
  window.addEventListener('pagehide', () => { observer?.disconnect(); observer = null; compactObserver?.disconnect(); compactObserver = null; });
  document.addEventListener('DOMContentLoaded', initialize, {once:true});
})();
