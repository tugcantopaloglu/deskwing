'use strict';
// Local ink and notes. Pointer Events pressure/coalesced samples are optional capabilities.
(function () {
  const STORAGE = 'deskwing.pen.v1';
  const MAX_STROKES = 1200, MAX_POINTS = 60000, MAX_NOTE = 8000, MAX_BYTES = 3500000;
  const WIDTH = 1600, HEIGHT = 1000, MAX_HISTORY = 60;
  const colors = ['#1b2530', '#295a95', '#8a355b', '#36704f', '#a85a16'];
  const clamp = (value, low, high) => Math.max(low, Math.min(high, value));
  const finite = value => typeof value === 'number' && Number.isFinite(value);
  function createDocument() { return {version:1, strokes:[], notes:'', penOnly:true, updatedAt:new Date().toISOString()}; }
  function normalizeDocument(value) {
    if (!value || typeof value !== 'object' || value.version !== 1 || !Array.isArray(value.strokes) || value.strokes.length > MAX_STROKES || typeof value.notes !== 'string' || value.notes.length > MAX_NOTE || typeof value.penOnly !== 'boolean') throw new Error('The saved scratchpad is not a supported document.');
    let count = 0;
    const strokes = value.strokes.map(stroke => {
      if (!stroke || !['pen','eraser'].includes(stroke.tool) || !/^#[0-9a-f]{6}$/i.test(stroke.color) || !finite(stroke.width) || stroke.width < 1 || stroke.width > 40 || !Array.isArray(stroke.points) || stroke.points.length < 1) throw new Error('A saved stroke is invalid.');
      count += stroke.points.length;
      if (count > MAX_POINTS) throw new Error('The saved scratchpad contains too many points.');
      const points = stroke.points.map(point => {
        if (!Array.isArray(point) || point.length !== 3 || !point.every(finite) || point.some(number => number < 0 || number > 1)) throw new Error('A saved point is invalid.');
        return [point[0],point[1],point[2]];
      });
      return {tool:stroke.tool,color:stroke.color.toLowerCase(),width:stroke.width,points};
    });
    return {version:1,strokes,notes:value.notes,penOnly:value.penOnly,updatedAt:typeof value.updatedAt === 'string' && value.updatedAt.length <= 40 ? value.updatedAt : ''};
  }
  function makePoint(event, rect) {
    const raw = finite(event.pressure) && event.pressure > 0 ? event.pressure : 0.5;
    return [Math.round(clamp((event.clientX - rect.left) / Math.max(1,rect.width),0,1)*100000)/100000, Math.round(clamp((event.clientY - rect.top) / Math.max(1,rect.height),0,1)*100000)/100000, Math.round(clamp(raw,0.05,1)*1000)/1000];
  }
  function mayDraw(pointerType, penOnly, penNearby) { return pointerType === 'pen' || pointerType === 'mouse' || pointerType === '' || pointerType === 'touch' && !penOnly && !penNearby; }
  function strokeWidth(width, pressure) { return width * (0.3 + clamp(finite(pressure) ? pressure : 0.5,0,1)*1.4); }
  function createHistory(initial) {
    let current = initial, past = [], future = [];
    return {
      get current() { return current; }, get canUndo() { return past.length > 0; }, get canRedo() { return future.length > 0; },
      commit(next) { past.push(current); if (past.length > MAX_HISTORY) past.shift(); current = next; future = []; return current; },
      undo() { if (past.length) { future.push(current); current = past.pop(); } return current; },
      redo() { if (future.length) { past.push(current); current = future.pop(); } return current; }
    };
  }
  window.DeskPenCore = Object.freeze({normalizeDocument,createDocument,makePoint,mayDraw,strokeWidth,createHistory,constants:Object.freeze({MAX_STROKES,MAX_POINTS,MAX_NOTE,MAX_BYTES})});

  let doc = createDocument(), history = createHistory(doc.strokes), loaded = false, loadError = '', saveError = '', active = false, suspended = false;
  let host, canvas, context, noteField, stateLine, emptyHint, undoButton, redoButton, clearButton, penButton, eraserButton, saveTimer, resizeObserver;
  let inkCache, cachedStrokes = null, committedPoints = 0;
  let currentStroke = null, activePointer = null, activePointerType = '', tool = 'pen', color = colors[0], width = 3, framePending = false, handwritingPending = false;
  let penNearbyUntil = 0, palmSuppressedUntil = 0, focusTarget = null, handwriteFieldButton = null;
  const penPointers = new Set(), rejectedTouches = new Set();
  const now = () => Date.now();
  const isNative = () => typeof native !== 'undefined' && native === true && typeof nativeRequest === 'function';
  const tell = (message, error = false) => { if (stateLine && active) { stateLine.textContent = message; stateLine.classList.toggle('pen-error',error); } if (typeof notice === 'function') notice(message,error); };
  const element = (tag, className, text) => { const node = document.createElement(tag); if (className) node.className = className; if (text !== undefined) node.textContent = text; return node; };
  function button(label, action, className = 'quiet') { const node = element('button',className,label); node.type = 'button'; node.addEventListener('click',action); return node; }
  function nearby() { return penPointers.size > 0 || now() < penNearbyUntil; }
  function countPoints(strokes = doc.strokes) { return strokes.reduce((total,stroke) => total + stroke.points.length,0); }
  function load() {
    if (loaded) return; loaded = true;
    try {
      const raw = localStorage.getItem(STORAGE);
      if (raw) { if (raw.length > MAX_BYTES) throw new Error('The saved scratchpad exceeds the storage limit.'); doc = normalizeDocument(JSON.parse(raw)); }
    } catch (error) { loadError = 'The saved scratchpad could not be opened. It has been kept unchanged. Start a new scratchpad to replace it.'; }
    history = createHistory(doc.strokes); committedPoints = countPoints();
  }
  function persist() {
    clearTimeout(saveTimer); saveTimer = null;
    if (!loaded || loadError) return false;
    doc.updatedAt = new Date().toISOString();
    try {
      const serialized = JSON.stringify(doc);
      if (serialized.length > MAX_BYTES) throw new Error('The scratchpad is full.');
      localStorage.setItem(STORAGE,serialized); saveError = '';
      if (stateLine && !handwritingPending) { stateLine.textContent = 'Saved on this device · drawings and notes stay local'; stateLine.classList.remove('pen-error'); }
      return true;
    } catch (error) {
      saveError = 'Could not save on this device. Keep this view open and export your drawing and notes before leaving.';
      if (stateLine) { stateLine.textContent = saveError; stateLine.classList.add('pen-error'); }
      return false;
    }
  }
  function scheduleSave() { clearTimeout(saveTimer); saveTimer = setTimeout(persist,350); if (stateLine) stateLine.textContent = 'Saving on this device…'; }
  function controls() {
    if (!canvas) return;
    const blocked = !!currentStroke || !!loadError;
    undoButton.disabled = blocked || !history.canUndo; redoButton.disabled = blocked || !history.canRedo;
    clearButton.disabled = blocked || doc.strokes.length === 0;
    penButton.setAttribute('aria-pressed',String(tool === 'pen')); eraserButton.setAttribute('aria-pressed',String(tool === 'eraser'));
    emptyHint.hidden = doc.strokes.length > 0 || !!currentStroke || !!loadError;
    canvas.setAttribute('aria-label',`Drawing canvas. ${doc.strokes.length} strokes. ${doc.penOnly ? 'Fingers are ignored; use a pen or mouse.' : 'Pen, mouse, or one finger can draw.'}`);
  }
  function drawStroke(ctx, stroke, pixelWidth, pixelHeight) {
    const scale = pixelWidth / WIDTH;
    ctx.save(); ctx.globalCompositeOperation = stroke.tool === 'eraser' ? 'destination-out' : 'source-over'; ctx.strokeStyle = stroke.color; ctx.fillStyle = stroke.color; ctx.lineCap = 'round'; ctx.lineJoin = 'round';
    const first = stroke.points[0];
    ctx.beginPath(); ctx.arc(first[0]*pixelWidth,first[1]*pixelHeight,strokeWidth(stroke.width,first[2])*scale/2,0,Math.PI*2); ctx.fill();
    for (let i=1; i<stroke.points.length; i++) { const previous = stroke.points[i-1], point = stroke.points[i]; ctx.lineWidth = strokeWidth(stroke.width,(previous[2]+point[2])/2)*scale; ctx.beginPath(); ctx.moveTo(previous[0]*pixelWidth,previous[1]*pixelHeight); ctx.lineTo(point[0]*pixelWidth,point[1]*pixelHeight); ctx.stroke(); }
    ctx.restore();
  }
  function redraw() {
    framePending = false;
    if (!canvas || !context || !active) return;
    if (!inkCache) inkCache = document.createElement('canvas');
    if (inkCache.width !== canvas.width || inkCache.height !== canvas.height || cachedStrokes !== doc.strokes) {
      inkCache.width = canvas.width; inkCache.height = canvas.height;
      const cachedContext = inkCache.getContext('2d');
      for (const stroke of doc.strokes) drawStroke(cachedContext,stroke,canvas.width,canvas.height);
      cachedStrokes = doc.strokes;
    }
    context.clearRect(0,0,canvas.width,canvas.height); context.drawImage(inkCache,0,0);
    if (currentStroke) drawStroke(context,currentStroke,canvas.width,canvas.height);
  }
  function scheduleDraw() { if (framePending || !active) return; framePending = true; requestAnimationFrame(redraw); }
  function resize() {
    if (!canvas || !active) return;
    const rect = canvas.getBoundingClientRect(), ratio = Math.min(window.devicePixelRatio || 1,2);
    const nextWidth = Math.max(1,Math.min(3200,Math.round(rect.width*ratio))), nextHeight = Math.max(1,Math.round(nextWidth*HEIGHT/WIDTH));
    if (canvas.width !== nextWidth || canvas.height !== nextHeight) { canvas.width = nextWidth; canvas.height = nextHeight; }
    scheduleDraw();
  }
  function releasePointer(pointerId) { try { if (canvas?.hasPointerCapture(pointerId)) canvas.releasePointerCapture(pointerId); } catch {} }
  function cancelStroke() {
    const pointerId = activePointer; currentStroke = null; activePointer = null; activePointerType = '';
    if (pointerId !== null) releasePointer(pointerId);
    controls(); scheduleDraw();
  }
  function addSample(event) {
    if (!currentStroke) return;
    if (committedPoints + currentStroke.points.length >= MAX_POINTS) return;
    const point = makePoint(event,canvas.getBoundingClientRect()), last = currentStroke.points[currentStroke.points.length-1];
    if (!last || Math.hypot((point[0]-last[0])*WIDTH,(point[1]-last[1])*HEIGHT) >= 0.25 || Math.abs(point[2]-last[2]) > 0.04) currentStroke.points.push(point);
  }
  function beginStroke(event) {
    if (!active || suspended || loadError || document.hidden || (event.button !== 0 && !(event.pointerType === 'pen' && [2,5].includes(event.button)))) return;
    if (!mayDraw(event.pointerType,doc.penOnly,nearby())) { event.preventDefault(); return; }
    if (activePointer !== null) { if (event.pointerType === 'pen' && activePointerType === 'touch') cancelStroke(); else return; }
    if (doc.strokes.length >= MAX_STROKES || committedPoints >= MAX_POINTS) { tell('This page is full. Export it, then clear the drawing to keep sketching.',true); event.preventDefault(); return; }
    const erasing = tool === 'eraser' || event.pointerType === 'pen' && (event.button === 5 || (event.buttons & 34) !== 0);
    currentStroke = {tool:erasing ? 'eraser' : 'pen',color,width:erasing ? Math.max(12,width*4) : width,points:[]};
    activePointer = event.pointerId; activePointerType = event.pointerType;
    try { canvas.setPointerCapture(event.pointerId); } catch {}
    event.preventDefault(); addSample(event); controls(); scheduleDraw();
  }
  function moveStroke(event) {
    if (activePointer !== event.pointerId || !currentStroke) return;
    event.preventDefault();
    const samples = typeof event.getCoalescedEvents === 'function' ? event.getCoalescedEvents() : [];
    for (const sample of samples.length ? samples : [event]) addSample(sample);
    scheduleDraw();
  }
  function finishStroke(event) {
    if (activePointer !== event.pointerId || !currentStroke) return;
    event.preventDefault(); addSample(event);
    const stroke = currentStroke, pointerId = activePointer; currentStroke = null; activePointer = null; activePointerType = '';
    releasePointer(pointerId);
    if (stroke.points.length) { doc.strokes = history.commit([...doc.strokes,stroke]); committedPoints += stroke.points.length; }
    controls(); scheduleDraw(); persist();
    if (committedPoints >= MAX_POINTS) tell('This page is full. Your last stroke was saved. Export, then clear the drawing to continue.',true);
  }
  function undo() { if (currentStroke || loadError) return; doc.strokes = history.undo(); committedPoints=countPoints(); controls(); scheduleDraw(); persist(); }
  function redo() { if (currentStroke || loadError) return; doc.strokes = history.redo(); committedPoints=countPoints(); controls(); scheduleDraw(); persist(); }
  async function clearDrawing() {
    if (currentStroke || loadError || !doc.strokes.length) return;
    if (typeof confirmAction === 'function' && !await confirmAction('Clear the drawing?','Your notes stay. You can undo this while the scratchpad is open.')) return;
    doc.strokes = history.commit([]); committedPoints=0; controls(); scheduleDraw(); persist();
  }
  async function replaceUnreadable() {
    if (typeof confirmAction === 'function' && !await confirmAction('Replace the unreadable scratchpad?','This replaces the saved local drawing and notes with a blank scratchpad.')) return;
    loadError = ''; doc = createDocument(); history = createHistory(doc.strokes); committedPoints=0; host.replaceChildren(); mount(); persist(); resize();
  }
  function buildExport() {
    const ink = document.createElement('canvas'); ink.width = WIDTH; ink.height = HEIGHT;
    const inkContext = ink.getContext('2d'); if (!inkContext) throw new Error('Canvas export is unavailable.');
    for (const stroke of doc.strokes) drawStroke(inkContext,stroke,WIDTH,HEIGHT);
    const exported = document.createElement('canvas'); exported.width = WIDTH; exported.height = HEIGHT;
    const exportContext = exported.getContext('2d'); if (!exportContext) throw new Error('Canvas export is unavailable.');
    exportContext.fillStyle = '#f6f3e9'; exportContext.fillRect(0,0,WIDTH,HEIGHT); exportContext.drawImage(ink,0,0);
    return exported.toDataURL('image/png');
  }
  function download(data, name) {
    const link = element('a'); link.href = data; link.download = name; document.body.appendChild(link); link.click(); link.remove();
  }
  async function exportDrawing() {
    if (currentStroke) { tell('Lift the pen before exporting.',true); return; }
    const exportButton = document.getElementById('pen-export'); if (exportButton?.disabled) return;
    if (exportButton) exportButton.disabled = true;
    try {
      const dataUrl = buildExport();
      if (dataUrl.length > 11000000) throw new Error('This drawing is too large to export.');
      if (isNative()) { const result = await nativeRequest('/local/export-image',{dataUrl,name:'DeskWing-sketch.png'},'POST',0); if (result.cancelled) return; if (!result.saved) throw new Error('The drawing was not saved.'); tell('Drawing exported. Your local scratchpad is unchanged.'); }
      else { download(dataUrl,'DeskWing-sketch.png'); tell('Drawing export started. Your local scratchpad is unchanged.'); }
    } catch (error) { tell(error.message || 'Could not export the drawing. Your local scratchpad is unchanged.',true); }
    finally { if (exportButton?.isConnected) exportButton.disabled = false; }
  }
  async function exportNotes() {
    if (!doc.notes) { tell('Add a note first.'); return; }
    try {
      if (navigator.clipboard?.writeText) { await navigator.clipboard.writeText(doc.notes); tell('Notes copied. Paste them into the app you choose.'); }
      else { noteField.focus(); noteField.select(); tell('Notes selected. Use your keyboard’s Copy command.'); }
    } catch { noteField.focus(); noteField.select(); tell('Notes selected. Use your keyboard’s Copy command.'); }
  }
  function writable(target) { return !!target && target.isConnected && !target.disabled && !target.readOnly && (target.tagName === 'TEXTAREA' || target.tagName === 'INPUT' && ['text','search','url','email','tel'].includes(target.type)) && !target.closest('[hidden]'); }
  async function requestHandwriting(target = focusTarget || noteField) {
    if (handwritingPending || !writable(target)) return false;
    if (!isNative()) { target.focus(); tell('Handwriting entry uses the Android tablet’s keyboard. Here, type or paste text instead.'); return false; }
    const original = target.value;
    if (original.length > MAX_NOTE) { tell('Handwriting entry supports up to 8,000 characters. Shorten this field first.',true); return false; }
    handwritingPending = true;
    document.querySelectorAll('[data-pen-handwrite]').forEach(node => node.disabled = true);
    tell('Write in the Android handwriting panel. Use its keyboard if handwriting is unavailable.');
    try {
      const result = await nativeRequest('/local/handwriting',{text:original},'POST',0);
      if (result?.cancelled) { if (active) tell('Handwriting cancelled. Your text is unchanged.'); return false; }
      if (!result || typeof result.text !== 'string' || result.text.length > MAX_NOTE) throw new Error('Handwriting returned invalid text. Your text is unchanged.');
      if (!writable(target) || target.value !== original) { tell('That field changed while handwriting was open. Its newer value was kept.',true); return false; }
      const limit = target.maxLength > -1 ? Math.min(target.maxLength,MAX_NOTE) : MAX_NOTE;
      if (result.text.length > limit) { tell(`This field allows ${limit} characters. Your existing text was kept.`,true); return false; }
      target.value = result.text; target.dispatchEvent(new Event('input',{bubbles:true})); target.dispatchEvent(new Event('change',{bubbles:true})); target.focus();
      tell('Handwritten text inserted. You can keep editing it.'); return true;
    } catch (error) { target?.isConnected && target.focus(); tell((error.message || 'Handwriting entry is unavailable.')+' Type or paste text in the field instead.',true); return false; }
    finally { handwritingPending = false; document.querySelectorAll('[data-pen-handwrite]').forEach(node => node.disabled = false); }
  }
  function mount() {
    if (canvas?.isConnected) return;
    host = document.getElementById('pen-workspace');
    if (!host) { host = element('main'); host.id = 'pen-workspace'; host.hidden = true; document.body.appendChild(host); }
    host.className = 'pen-workspace'; host.setAttribute('aria-label','Ink and notes');
    const heading = element('header','pen-heading'), copy = element('div'); copy.append(element('h2','','Ink & notes'),element('p','help','Sketch, write, and keep ideas beside your controls.')); heading.append(copy);
    const exportButton = button('Export drawing',exportDrawing); exportButton.id = 'pen-export'; heading.append(exportButton); host.append(heading);
    stateLine = element('p','pen-save-state',loadError || 'Saved on this device · drawings and notes stay local'); stateLine.id = 'pen-status'; stateLine.setAttribute('role','status'); host.append(stateLine);
    if (loadError) { stateLine.classList.add('pen-error'); const recovery = button('Start a new scratchpad',replaceUnreadable,'danger'); host.append(recovery); }
    const toolbar = element('div','pen-toolbar'); toolbar.setAttribute('role','toolbar'); toolbar.setAttribute('aria-label','Drawing tools');
    const tools = element('div','pen-tool-group'); penButton = button('Pen',()=>{tool='pen';controls();}); penButton.id='pen-tool'; eraserButton=button('Eraser',()=>{tool='eraser';controls();}); eraserButton.id='pen-eraser'; tools.append(penButton,eraserButton); toolbar.append(tools);
    const colorLabel = element('label','pen-color-label','Ink'); const colorInput = element('select'); colorInput.id='pen-color'; colorInput.setAttribute('aria-label','Ink color');
    for (const [index,name] of ['Graphite','Blue','Berry','Green','Ochre'].entries()) { const option = element('option','',name); option.value=colors[index]; colorInput.append(option); } colorInput.value=color; colorInput.onchange=()=>{color=colorInput.value;}; colorLabel.append(colorInput); toolbar.append(colorLabel);
    const widthLabel = element('label','pen-width-label','Width'); const widthInput = element('input'); widthInput.id='pen-width'; widthInput.type='range'; widthInput.min='1'; widthInput.max='10'; widthInput.step='1'; widthInput.value=String(width); widthInput.setAttribute('aria-label','Pen width'); const widthOutput=element('output','',String(width)); widthOutput.htmlFor='pen-width'; widthInput.oninput=()=>{width=Number(widthInput.value);widthOutput.textContent=String(width);}; widthLabel.append(widthInput,widthOutput); toolbar.append(widthLabel);
    const historyTools=element('div','pen-tool-group'); undoButton=button('Undo',undo);undoButton.id='pen-undo';redoButton=button('Redo',redo);redoButton.id='pen-redo';clearButton=button('Clear',clearDrawing);clearButton.id='pen-clear';historyTools.append(undoButton,redoButton,clearButton);toolbar.append(historyTools);
    const palm=element('label','pen-palm-toggle'),palmInput=element('input');palmInput.type='checkbox';palmInput.id='pen-only';palmInput.checked=doc.penOnly;palmInput.onchange=()=>{doc.penOnly=palmInput.checked;if(currentStroke&&activePointerType==='touch')cancelStroke();persist();controls();};palm.append(palmInput,element('span','','Ignore fingers on canvas'));toolbar.append(palm);host.append(toolbar);
    const body=element('div','pen-body'),drawing=element('section','pen-drawing');drawing.setAttribute('aria-label','Scratchpad drawing');const paper=element('div','pen-paper');
    canvas=element('canvas');canvas.id='pen-canvas';canvas.width=WIDTH;canvas.height=HEIGHT;canvas.tabIndex=0;canvas.setAttribute('role','img');canvas.setAttribute('aria-describedby','pen-canvas-help');context=canvas.getContext('2d');
    emptyHint=element('div','pen-empty');emptyHint.append(element('strong','','Room for your next idea'),element('span','','Draw with your pen. Your strokes save here.'));paper.append(canvas,emptyHint);drawing.append(paper);
    const help=element('p','help','Pressure changes ink width when your pen supports it. Ignore fingers keeps palm marks off this canvas.');help.id='pen-canvas-help';drawing.append(help);body.append(drawing);
    const notes=element('section','pen-notes');notes.setAttribute('aria-label','Text notes');const notesHeading=element('div','pen-notes-heading');notesHeading.append(element('label','','Notes'));noteField=element('textarea');noteField.id='pen-notes';noteField.maxLength=MAX_NOTE;noteField.value=doc.notes;noteField.placeholder='An idea, a script, or the next step…';noteField.setAttribute('aria-label','Notes');noteField.disabled=!!loadError;notesHeading.firstChild.htmlFor='pen-notes';
    const handwritingButton=button('Handwrite text',()=>requestHandwriting(noteField));handwritingButton.dataset.penHandwrite='notes';handwritingButton.id='pen-handwriting';handwritingButton.disabled=!!loadError;notesHeading.append(handwritingButton);notes.append(notesHeading,noteField);
    noteField.oninput=()=>{doc.notes=noteField.value.slice(0,MAX_NOTE);scheduleSave();};const noteActions=element('div','pen-note-actions');noteActions.append(button('Copy notes',exportNotes),element('span','help','Up to 8,000 characters'));notes.append(noteActions,element('p','help',isNative()?'Handwriting recognition uses your Android keyboard. You can always type in the same panel.':'On the Android tablet, Handwrite text opens your keyboard’s handwriting panel. Here, type or paste.'));body.append(notes);host.append(body);
    canvas.addEventListener('pointerdown',beginStroke);canvas.addEventListener('pointermove',moveStroke);canvas.addEventListener('pointerup',finishStroke);canvas.addEventListener('pointercancel',event=>{if(event.pointerId===activePointer)cancelStroke();});canvas.addEventListener('lostpointercapture',event=>{if(event.pointerId===activePointer)cancelStroke();});canvas.addEventListener('contextmenu',event=>event.preventDefault());
    canvas.addEventListener('keydown',event=>{if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='z'){event.preventDefault();event.shiftKey?redo():undo();}else if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='y'){event.preventDefault();redo();}});
    if (typeof ResizeObserver !== 'undefined') { resizeObserver?.disconnect();resizeObserver=new ResizeObserver(resize);resizeObserver.observe(canvas); }
    controls();
  }
  function setActive(value) {
    const next = value === true || value === 'pen';
    if (next) { load(); active=true; suspended=false; mount(); host.hidden=false; document.body.classList.add('pen-mode'); resize(); }
    else { cancelStroke();persist();active=false;if(host)host.hidden=true;document.body.classList.remove('pen-mode'); }
    return true;
  }
  function suspend() { suspended=true;cancelStroke();persist();penPointers.clear();rejectedTouches.clear();penNearbyUntil=0; }
  function resume() { suspended=false;if(active){resize();controls();} }
  function palmGuard(event) {
    if (event.pointerType === 'pen') {
      penNearbyUntil=now()+350;
      if(event.type==='pointerdown') {penPointers.add(event.pointerId);if(currentStroke&&activePointerType==='touch')cancelStroke();}
      if(event.type==='pointerup'||event.type==='pointercancel')penPointers.delete(event.pointerId);
      return;
    }
    if(event.pointerType!=='touch')return;
    const blocked=rejectedTouches.has(event.pointerId)||(event.type==='pointerdown'&&nearby());
    if(blocked){rejectedTouches.add(event.pointerId);palmSuppressedUntil=now()+500;event.preventDefault();event.stopImmediatePropagation();if(event.type==='pointerup'||event.type==='pointercancel')rejectedTouches.delete(event.pointerId);}
  }
  function initialize() {
    for(const name of ['pointerdown','pointermove','pointerup','pointercancel','pointerover'])document.addEventListener(name,palmGuard,{capture:true,passive:false});
    document.addEventListener('click',event=>{if(event.pointerType==='touch'&&now()<palmSuppressedUntil){event.preventDefault();event.stopImmediatePropagation();}},{capture:true});
    document.addEventListener('visibilitychange',()=>document.hidden?suspend():resume());window.addEventListener('pagehide',suspend);window.addEventListener('blur',suspend);window.addEventListener('focus',resume);window.addEventListener('resize',resize);
    window.addEventListener('beforeunload',event=>{if(saveError){event.preventDefault();event.returnValue='';}});
    const priorSuspend=window.monitorSuspend,priorResume=window.monitorResume;
    window.monitorSuspend=(...args)=>{suspend();return priorSuspend?.apply(window,args);};window.monitorResume=(...args)=>{const result=priorResume?.apply(window,args);resume();return result;};
    if (isNative()) {
      handwriteFieldButton=button('Handwrite',()=>requestHandwriting(focusTarget),'quiet pen-field-handwrite');handwriteFieldButton.hidden=true;handwriteFieldButton.dataset.penHandwrite='field';handwriteFieldButton.setAttribute('aria-label','Handwrite in the selected text field');document.body.append(handwriteFieldButton);
      handwriteFieldButton.addEventListener('pointerdown',event=>event.preventDefault());
      document.addEventListener('focusin',event=>{if(event.target===handwriteFieldButton)return;focusTarget=writable(event.target)&&event.target!==noteField?event.target:null;handwriteFieldButton.hidden=!focusTarget;});
      document.addEventListener('pointerdown',event=>{if(focusTarget&&event.target!==handwriteFieldButton&&!event.target.closest('input,textarea')){focusTarget=null;handwriteFieldButton.hidden=true;}},{passive:true});
    }
  }
  window.DeskPen=Object.freeze({setActive,isActive:()=>active,suspend,resume,requestHandwriting});
  window.setPenMode=async value=>setActive(value);
  document.addEventListener('DOMContentLoaded',initialize,{once:true});
})();
