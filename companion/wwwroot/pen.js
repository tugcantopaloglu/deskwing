'use strict';
// Local ink backup with revision-checked sharing to the paired PC. Pressure data is optional.
(function () {
  const STORAGE = 'deskwing.pen.v1';
  const SYNC_STORAGE = 'deskwing.pen.sync.v1', RECOVERY_STORAGE = 'deskwing.pen.recovery.v1';
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
  function contentKey(value) { return JSON.stringify({strokes:value.strokes,notes:value.notes}); }
  function isEmpty(value) { return value.strokes.length === 0 && value.notes.length === 0; }
  function fitPaper(availableWidth, availableHeight) { const fittedWidth=Math.max(0,Math.min(availableWidth,availableHeight*WIDTH/HEIGHT));return {width:fittedWidth,height:fittedWidth*HEIGHT/WIDTH}; }
  function byteLength(value) { return typeof TextEncoder !== 'undefined' ? new TextEncoder().encode(value).length : unescape(encodeURIComponent(value)).length; }
  window.DeskPenCore = Object.freeze({normalizeDocument,createDocument,makePoint,mayDraw,strokeWidth,createHistory,contentKey,isEmpty,fitPaper,constants:Object.freeze({MAX_STROKES,MAX_POINTS,MAX_NOTE,MAX_BYTES})});

  let doc = createDocument(), history = createHistory(doc.strokes), loaded = false, loadError = '', saveError = '', active = false, suspended = false;
  let host, canvas, context, noteField, stateLine, emptyHint, undoButton, redoButton, clearButton, penButton, eraserButton, saveTimer, resizeObserver;
  let inkCache, cachedStrokes = null, committedPoints = 0;
  let paperNode, drawingSection, notesSection, fullscreenButton, fullscreenToolsButton, toolBox, notesToggle, fullscreen=false, fullscreenNotes=false, fullscreenTools=false, browserFullscreen=false, nativeFullscreen=false, fullscreenEpoch=0;
  let syncLine, syncDetails, syncConflictBox, syncRetryButton, syncRestoreButton, syncPollTimer=null, syncPending=false, syncEpoch=0, choicePending=false;
  let syncPhase='local', syncMessage='', syncRevision=null, syncWorkspaceId=null, syncBaseKey=null, syncConflict=null, syncMetadata=null, lastRemote=null, forceReview=false;
  let currentStroke = null, activePointer = null, activePointerType = '', tool = 'pen', color = colors[0], width = 3, framePending = false, handwritingPending = false;
  let penNearbyUntil = 0, palmSuppressedUntil = 0, focusTarget = null, handwriteFieldButton = null;
  const penPointers = new Set(), rejectedTouches = new Set();
  const now = () => Date.now();
  const isNative = () => typeof native !== 'undefined' && native === true && typeof nativeRequest === 'function';
  const isIOS = () => window.DeckCapabilities?.platform === 'ios';
  const tell = (message, error = false) => { if (stateLine && active) { stateLine.textContent = message; stateLine.classList.toggle('pen-error',error); } if (typeof notice === 'function') notice(message,error); };
  const element = (tag, className, text) => { const node = document.createElement(tag); if (className) node.className = className; if (text !== undefined) node.textContent = text; return node; };
  function button(label, action, className = 'quiet') { const node = element('button',className,label); node.type = 'button'; node.addEventListener('click',action); return node; }
  function nearby() { return penPointers.size > 0 || now() < penNearbyUntil; }
  function countPoints(strokes = doc.strokes) { return strokes.reduce((total,stroke) => total + stroke.points.length,0); }
  function load() {
    if (loaded) return; loaded = true;
    try {
      const raw = localStorage.getItem(STORAGE);
      if (raw) { if (byteLength(raw) > MAX_BYTES) throw new Error('The saved scratchpad exceeds the storage limit.'); doc = normalizeDocument(JSON.parse(raw)); }
    } catch (error) { loadError = 'The saved scratchpad could not be opened. It has been kept unchanged. Start a new scratchpad to replace it.'; }
    history = createHistory(doc.strokes); committedPoints = countPoints();
    try {const raw=localStorage.getItem(SYNC_STORAGE);if(raw&&raw.length<1000){const meta=JSON.parse(raw);if(typeof meta.workspaceId==='string'&&typeof meta.revision==='string'&&typeof meta.hash==='string')syncMetadata=meta;}}catch{}
  }
  function persist() {
    clearTimeout(saveTimer); saveTimer = null;
    if (!loaded || loadError) return false;
    doc.updatedAt = new Date().toISOString();
    try {
      const serialized = JSON.stringify(doc);
      if (byteLength(serialized) > MAX_BYTES) throw new Error('The scratchpad is full.');
      localStorage.setItem(STORAGE,serialized); saveError = '';
      if (stateLine && !handwritingPending) { stateLine.textContent = 'Saved on this device · local backup kept'; stateLine.classList.remove('pen-error'); }
      queueSync(450);
      return true;
    } catch (error) {
      saveError = 'Could not save on this device. Keep this view open and export your drawing and notes before leaving.';
      if (stateLine) { stateLine.textContent = saveError; stateLine.classList.add('pen-error'); }
      return false;
    }
  }
  function scheduleSave() { clearTimeout(saveTimer); saveTimer = setTimeout(persist,350); if (stateLine) stateLine.textContent = 'Saving on this device…'; }
  function canSync() {return active&&!suspended&&!document.hidden&&!loadError&&typeof api==='function';}
  async function confirmInkChoice(title,copy) {
    if(choicePending)return false;choicePending=true;clearTimeout(syncPollTimer);syncPollTimer=null;renderSyncState();
    try{return typeof confirmAction!=='function'||await confirmAction(title,copy);}
    finally{choicePending=false;renderSyncState();queueSync();}
  }
  function queueSync(delay=1200) {clearTimeout(syncPollTimer);syncPollTimer=null;if(canSync())syncPollTimer=setTimeout(()=>{syncPollTimer=null;syncShared();},delay);}
  async function documentHash(value) {
    if(!window.crypto?.subtle||typeof TextEncoder==='undefined')return null;
    const digest=await window.crypto.subtle.digest('SHA-256',new TextEncoder().encode(contentKey(value)));
    return [...new Uint8Array(digest)].map(byte=>byte.toString(16).padStart(2,'0')).join('');
  }
  function validateSnapshot(snapshot) {
    if(!snapshot||typeof snapshot.workspaceId!=='string'||snapshot.workspaceId.length<1||snapshot.workspaceId.length>100||typeof snapshot.revision!=='string'||snapshot.revision.length<1||snapshot.revision.length>100)throw new Error('The PC returned an invalid ink version.');
    const document=normalizeDocument(snapshot.document);
    if(byteLength(JSON.stringify(document))>MAX_BYTES)throw new Error('The PC scratchpad exceeds the supported size.');
    return {workspaceId:snapshot.workspaceId,revision:snapshot.revision,document,recoveryWarning:typeof snapshot.recoveryWarning==='string'?snapshot.recoveryWarning.slice(0,500):''};
  }
  function setSyncState(phase,message='') {syncPhase=phase;syncMessage=message;renderSyncState();}
  function renderSyncState() {
    if(!syncLine)return;
    const labels={local:'Local scratchpad',checking:'Checking PC copy…',synced:'Shared with your PC',pending:'Sharing changes…',offline:'Offline · local copy kept',conflict:'Two versions need your choice'};
    syncLine.textContent=labels[syncPhase]||labels.local;
    syncDetails.textContent=syncMessage||(syncPhase==='synced'?'Drawings and notes appear in Ink & notes on both devices.':syncPhase==='local'?'Connect to the companion to share drawings and notes.':'');
    syncRetryButton.disabled=syncPending||choicePending||!!loadError;syncRetryButton.textContent=syncPhase==='offline'?'Retry sync':'Sync now';
    let recovery=false;try{recovery=!!localStorage.getItem(RECOVERY_STORAGE);}catch{}syncRestoreButton.hidden=!recovery;
    syncConflictBox.hidden=!syncConflict;
    const detail=syncConflictBox.querySelector?.('[data-pen-conflict-detail]');
    if(detail&&syncConflict)detail.textContent=`This device: ${doc.strokes.length} strokes, ${doc.notes.length} note characters. PC: ${syncConflict.document.strokes.length} strokes, ${syncConflict.document.notes.length} note characters. Nothing has been replaced.`;
    syncConflictBox.querySelectorAll?.('button').forEach(node=>node.disabled=syncPending||choicePending||!!currentStroke||handwritingPending);
    if(fullscreenToolsButton){const warning=syncPhase==='conflict'||syncPhase==='offline'||!!saveError;fullscreenToolsButton.classList.toggle('pen-tools-warning',warning);fullscreenToolsButton.setAttribute('aria-label',(fullscreenTools?'Hide tools':'Show tools')+(warning?' — ink sharing needs attention':''));}
  }
  async function rememberSync(snapshot,content) {
    syncWorkspaceId=snapshot.workspaceId;syncRevision=snapshot.revision;syncBaseKey=contentKey(content);lastRemote=snapshot;
    const hash=await documentHash(content);
    if(hash&&syncWorkspaceId===snapshot.workspaceId&&syncRevision===snapshot.revision){syncMetadata={workspaceId:snapshot.workspaceId,revision:snapshot.revision,hash};try{localStorage.setItem(SYNC_STORAGE,JSON.stringify(syncMetadata));}catch{}}
  }
  function currentForSync(epoch,key) {return canSync()&&syncEpoch===epoch&&!currentStroke&&!handwritingPending&&!choicePending&&contentKey(doc)===key;}
  function preserveRecovery(value) {
    try{const text=JSON.stringify(value);if(byteLength(text)>MAX_BYTES)throw new Error('Scratchpad is too large.');localStorage.setItem(RECOVERY_STORAGE,text);return true;}
    catch{tell('There is not enough local storage to keep both copies. Export your drawing and copy your notes before replacing anything.',true);return false;}
  }
  function applyDocument(value) {
    const penOnly=doc.penOnly;doc=normalizeDocument(value);doc.penOnly=penOnly;history=createHistory(doc.strokes);committedPoints=countPoints();
    if(noteField)noteField.value=doc.notes;controls();scheduleDraw();persist();
  }
  function conflictWith(snapshot) {syncConflict=snapshot;lastRemote=snapshot;setSyncState('conflict','Choose which version to use. Your local copy will be kept before loading the PC version.');}
  async function adoptRemote(snapshot,epoch,key) {
    if(!currentForSync(epoch,key))return;
    applyDocument(snapshot.document);syncConflict=null;forceReview=false;
    await rememberSync(snapshot,snapshot.document);
    if(canSync()&&syncEpoch===epoch)setSyncState('synced',snapshot.recoveryWarning);
  }
  async function pushDocument(snapshot,epoch) {
    if(!canSync()||syncEpoch!==epoch||currentStroke||handwritingPending)return;
    const outgoing=normalizeDocument(doc);outgoing.updatedAt=new Date().toISOString();const sentKey=contentKey(outgoing);
    if(byteLength(JSON.stringify(outgoing))>MAX_BYTES)throw new Error('The scratchpad is too large to share. Export it and start a new page.');
    setSyncState('pending');
    const saved=validateSnapshot(await api('/api/ink',{expectedRevision:snapshot.revision,document:outgoing},15000));
    if(saved.workspaceId!==snapshot.workspaceId)throw new Error('The PC ink workspace changed. Recheck both copies before sharing.');
    // A new stroke may have arrived during the request. Record its baseline without replacing it.
    await rememberSync(saved,outgoing);
    syncConflict=null;forceReview=false;
    if(canSync()&&syncEpoch===epoch)setSyncState(contentKey(doc)===sentKey?'synced':'pending',saved.recoveryWarning);
  }
  async function syncShared() {
    clearTimeout(syncPollTimer);syncPollTimer=null;
    if(!canSync()||syncPending)return;
    if(choicePending){queueSync();return;}
    if(currentStroke||handwritingPending){queueSync();return;}
    const epoch=syncEpoch;syncPending=true;renderSyncState();
    try {
      const known=lastRemote&&/^[0-9a-f]{64}$/i.test(lastRemote.revision)?lastRemote:null;
      const response=await api('/api/ink'+(known?'?revision='+encodeURIComponent(known.revision):''),undefined,12000);
      let snapshot;
      if(response?.unchanged===true){
        if(!known||response.workspaceId!==known.workspaceId||response.revision!==known.revision)throw new Error('The PC returned an unexpected ink version. Sync again.');
        snapshot={...known,recoveryWarning:typeof response.recoveryWarning==='string'?response.recoveryWarning.slice(0,500):''};
      }else snapshot=validateSnapshot(response);
      if(!canSync()||syncEpoch!==epoch||currentStroke||handwritingPending||choicePending)return;
      const localKey=contentKey(doc),remoteKey=contentKey(snapshot.document);lastRemote=snapshot;
      if(localKey===remoteKey){syncConflict=null;forceReview=false;await rememberSync(snapshot,doc);if(currentForSync(epoch,localKey))setSyncState('synced',snapshot.recoveryWarning);return;}
      if(forceReview||syncConflict){conflictWith(snapshot);return;}
      if(syncWorkspaceId===snapshot.workspaceId&&syncBaseKey!==null){
        if(localKey===syncBaseKey){await adoptRemote(snapshot,epoch,localKey);return;}
        if(snapshot.revision===syncRevision||remoteKey===syncBaseKey){await pushDocument(snapshot,epoch);return;}
        conflictWith(snapshot);return;
      }
      if(syncMetadata?.workspaceId===snapshot.workspaceId){
        const localHash=await documentHash(doc);if(!currentForSync(epoch,localKey))return;
        if(localHash&&localHash===syncMetadata.hash){await adoptRemote(snapshot,epoch,localKey);return;}
        if(snapshot.revision===syncMetadata.revision){await pushDocument(snapshot,epoch);return;}
        conflictWith(snapshot);return;
      }
      if(isEmpty(doc)){await adoptRemote(snapshot,epoch,localKey);return;}
      if(isEmpty(snapshot.document)){await pushDocument(snapshot,epoch);return;}
      conflictWith(snapshot);
    } catch(error) {if(canSync()&&syncEpoch===epoch)setSyncState('offline',(error.message||'The companion could not be reached.')+' Your drawing and notes are still on this device.');}
    finally{syncPending=false;renderSyncState();queueSync(syncPhase==='offline'?4000:1200);}
  }
  async function chooseRemote() {
    if(!syncConflict||syncPending||currentStroke||handwritingPending)return;
    const snapshot=syncConflict;
    if(!await confirmInkChoice('Use the PC version?','Your current drawing and notes will be kept as a local recovery copy. The PC version will open here.'))return;
    if(!canSync()||syncConflict?.workspaceId!==snapshot.workspaceId||syncConflict?.revision!==snapshot.revision||currentStroke||handwritingPending)return;
    if(!preserveRecovery(doc))return;
    const key=contentKey(doc),epoch=syncEpoch;await adoptRemote(snapshot,epoch,key);renderSyncState();
  }
  async function chooseLocal() {
    if(!syncConflict||syncPending||currentStroke||handwritingPending)return;
    const snapshot=syncConflict;
    if(!await confirmInkChoice('Use this version on the PC?','This replaces the PC drawing and notes with the version shown here. The previous PC version will be kept as a local recovery copy.'))return;
    if(!canSync()||syncConflict?.workspaceId!==snapshot.workspaceId||syncConflict?.revision!==snapshot.revision||currentStroke||handwritingPending)return;
    if(!preserveRecovery(snapshot.document))return;
    syncPending=true;renderSyncState();const epoch=syncEpoch;
    try{await pushDocument(snapshot,epoch);}
    catch(error){setSyncState('conflict',(error.message||'The PC version changed.')+' Nothing was overwritten. Sync again to review the newest PC copy.');}
    finally{syncPending=false;renderSyncState();queueSync();}
  }
  async function restoreRecovery() {
    if(syncPending||currentStroke||handwritingPending)return;
    let saved;try{const raw=localStorage.getItem(RECOVERY_STORAGE);if(!raw||byteLength(raw)>MAX_BYTES)throw new Error('Invalid saved copy.');saved=normalizeDocument(JSON.parse(raw));}catch{tell('The local recovery copy could not be read.',true);return;}
    if(!await confirmInkChoice('Open the local recovery copy?','Your current local view will take its place in recovery. You will choose explicitly before replacing the PC version.'))return;
    if(currentStroke||handwritingPending)return;if(!preserveRecovery(doc))return;
    forceReview=true;applyDocument(saved);syncConflict=lastRemote;setSyncState('conflict','Recovery copy opened. Choose explicitly before sharing it to the PC.');queueSync(0);
  }
  function controls() {
    if (!canvas) return;
    const blocked = !!currentStroke || !!loadError;
    undoButton.disabled = blocked || !history.canUndo; redoButton.disabled = blocked || !history.canRedo;
    clearButton.disabled = blocked || doc.strokes.length === 0;
    penButton.setAttribute('aria-pressed',String(tool === 'pen')); eraserButton.setAttribute('aria-pressed',String(tool === 'eraser'));
    emptyHint.hidden = doc.strokes.length > 0 || !!currentStroke || !!loadError;
    canvas.setAttribute('aria-label',`Drawing canvas. ${doc.strokes.length} strokes. ${doc.penOnly ? 'Fingers are ignored; use a pen or mouse.' : 'Pen, mouse, or one finger can draw.'}`);
    renderSyncState();
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
    if(paperNode&&drawingSection){
      if(fullscreen||document.body.classList.contains('ink-window-mode')){const available=drawingSection.getBoundingClientRect(),fit=fitPaper(available.width,available.height);paperNode.style.width=fit.width+'px';paperNode.style.height=fit.height+'px';}
      else{paperNode.style.width='';paperNode.style.height='';}
    }
    const rect = canvas.getBoundingClientRect(), ratio = Math.min(window.devicePixelRatio || 1,2);
    const nextWidth = Math.max(1,Math.min(3200,Math.round(rect.width*ratio))), nextHeight = Math.max(1,Math.round(nextWidth*HEIGHT/WIDTH));
    if (canvas.width !== nextWidth || canvas.height !== nextHeight) { if(currentStroke)cancelStroke();canvas.width = nextWidth; canvas.height = nextHeight; }
    scheduleDraw();
  }
  async function setFullscreen(value,options={}) {
    const next=value===true,wasFullscreen=fullscreen,ownedNative=nativeFullscreen,epoch=++fullscreenEpoch;
    if(next&&!active)return false;
    cancelStroke();persist();fullscreen=next;
    document.body.classList.toggle('pen-fullscreen',next);host?.classList.toggle('is-fullscreen',next);
    if(fullscreenButton){fullscreenButton.textContent=next?'Exit fullscreen':'Fullscreen';fullscreenButton.setAttribute('aria-pressed',String(next));}
    if(fullscreenToolsButton)fullscreenToolsButton.hidden=!next;
    setFullscreenTools(false);
    if(notesToggle)notesToggle.hidden=!next;
    if(notesSection)notesSection.hidden=next&&!fullscreenNotes;
    if(host)host.classList.toggle('pen-fullscreen-notes',next&&fullscreenNotes);
    resize();
    if(isNative()){
      // Restore system bars only if this workspace owned immersive mode. Other workspaces share the host.
      if(options.nativeEvent){nativeFullscreen=false;}
      else if(next||wasFullscreen||ownedNative){
        nativeFullscreen=next;
        try{
          const result=await nativeRequest('/local/fullscreen',{enabled:next},'POST',10000);
          if(epoch===fullscreenEpoch){nativeFullscreen=result?.enabled===true;if(next&&!nativeFullscreen)tell('The canvas is fullscreen, but the system bars could not be hidden.',true);}
        }catch(error){if(epoch===fullscreenEpoch){nativeFullscreen=false;if(next)tell('The canvas is fullscreen. The tablet could not hide system bars; exit and try again.',true);}}
      }
    }else if(next&&typeof host?.requestFullscreen==='function'){
      try{await host.requestFullscreen({navigationUI:'hide'});if(fullscreen&&active)browserFullscreen=document.fullscreenElement===host;else if(document.fullscreenElement===host)await document.exitFullscreen?.();}catch{/* In-app fullscreen remains available when the host does not support the browser API. */}
    }else if(!next&&document.fullscreenElement===host){try{await document.exitFullscreen?.();}catch{}}
    if(!next)browserFullscreen=false;
    resize();return true;
  }
  function setFullscreenTools(value) {
    fullscreenTools=fullscreen&&value===true;
    if(toolBox)toolBox.hidden=fullscreen&&!fullscreenTools;
    if(fullscreenToolsButton){fullscreenToolsButton.textContent=fullscreenTools?'Hide tools':'Show tools';fullscreenToolsButton.setAttribute('aria-expanded',String(fullscreenTools));}
    host?.classList.toggle('pen-tools-open',fullscreenTools);
    if(fullscreenTools)cancelStroke();
    else if(fullscreen&&toolBox?.contains?.(document.activeElement))fullscreenToolsButton?.focus({preventScroll:true});
    renderSyncState();
  }
  function toggleFullscreenNotes() {fullscreenNotes=!fullscreenNotes;if(notesSection)notesSection.hidden=fullscreen&&!fullscreenNotes;if(notesToggle){notesToggle.textContent=fullscreenNotes?'Hide notes':'Show notes';notesToggle.setAttribute('aria-pressed',String(fullscreenNotes));}host?.classList.toggle('pen-fullscreen-notes',fullscreen&&fullscreenNotes);cancelStroke();resize();}
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
    if (!await confirmInkChoice('Clear the drawing?','Your notes stay. You can undo this while the scratchpad is open.')) return;
    doc.strokes = history.commit([]); committedPoints=0; controls(); scheduleDraw(); persist();
  }
  async function replaceUnreadable() {
    if (!await confirmInkChoice('Replace the unreadable scratchpad?','This replaces the saved local drawing and notes with a blank scratchpad.')) return;
    await setFullscreen(false);
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
    if (!isNative()) { target.focus(); tell('Handwriting entry uses your tablet’s keyboard or Scribble. Here, type or paste text instead.'); return false; }
    const original = target.value;
    if (original.length > MAX_NOTE) { tell('Handwriting entry supports up to 8,000 characters. Shorten this field first.',true); return false; }
    handwritingPending = true;
    document.querySelectorAll('[data-pen-handwrite]').forEach(node => node.disabled = true);
    tell(isIOS()?'Write with Scribble in the text panel, or use its keyboard.':'Write in the Android handwriting panel. Use its keyboard if handwriting is unavailable.');
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
    const headingActions=element('div','pen-heading-actions');
    fullscreenButton=button('Fullscreen',()=>setFullscreen(!fullscreen));fullscreenButton.id='pen-fullscreen';fullscreenButton.setAttribute('aria-pressed','false');
    fullscreenToolsButton=button('Show tools',()=>setFullscreenTools(!fullscreenTools));fullscreenToolsButton.id='pen-fullscreen-tools';fullscreenToolsButton.hidden=true;fullscreenToolsButton.setAttribute('aria-expanded','false');fullscreenToolsButton.setAttribute('aria-controls','pen-toolbox');
    notesToggle=button('Show notes',toggleFullscreenNotes);notesToggle.id='pen-fullscreen-notes';notesToggle.hidden=true;notesToggle.setAttribute('aria-pressed','false');
    const exportButton = button('Export drawing',exportDrawing); exportButton.id = 'pen-export'; headingActions.append(exportButton,fullscreenToolsButton,fullscreenButton);heading.append(headingActions); host.append(heading);
    stateLine = element('p','pen-save-state',loadError || 'Saved on this device · local backup kept'); stateLine.id = 'pen-status'; stateLine.setAttribute('role','status'); host.append(stateLine);
    toolBox=element('div','pen-toolbox');toolBox.id='pen-toolbox';toolBox.setAttribute('aria-label','Ink tools and sharing');host.append(toolBox);toolBox.append(stateLine);
    const drawerActions=element('div','pen-drawer-actions');const drawerExport=button('Export drawing',exportDrawing);drawerExport.id='pen-fullscreen-export';drawerActions.append(notesToggle,drawerExport);toolBox.append(drawerActions);
    const syncBar=element('section','pen-sync-bar');syncBar.setAttribute('aria-label','PC ink sharing');const syncCopy=element('div','pen-sync-copy');syncLine=element('strong','','Local scratchpad');syncLine.id='pen-sync-state';syncLine.setAttribute('role','status');syncDetails=element('span','help','Connect to the companion to share drawings and notes.');syncCopy.append(syncLine,syncDetails);
    const syncActions=element('div','pen-sync-actions');syncRetryButton=button('Sync now',syncShared);syncRetryButton.id='pen-sync-now';syncRestoreButton=button('Recovery copy',restoreRecovery);syncRestoreButton.id='pen-recovery';syncRestoreButton.hidden=true;syncActions.append(syncRetryButton,syncRestoreButton);syncBar.append(syncCopy,syncActions);toolBox.append(syncBar);
    syncConflictBox=element('section','pen-conflict');syncConflictBox.hidden=true;syncConflictBox.setAttribute('aria-label','Choose an ink version');const conflictDetail=element('p');conflictDetail.dataset.penConflictDetail='';const conflictActions=element('div','pen-sync-actions');const usePC=button('Use PC version',chooseRemote),useLocal=button('Use this version on PC',chooseLocal);usePC.id='pen-use-pc';useLocal.id='pen-use-local';conflictActions.append(usePC,useLocal);syncConflictBox.append(conflictDetail,conflictActions);toolBox.append(syncConflictBox);
    if (loadError) { stateLine.classList.add('pen-error'); const recovery = button('Start a new scratchpad',replaceUnreadable,'danger'); toolBox.append(recovery); }
    const toolbar = element('div','pen-toolbar'); toolbar.setAttribute('role','toolbar'); toolbar.setAttribute('aria-label','Drawing tools');
    const tools = element('div','pen-tool-group'); penButton = button('Pen',()=>{tool='pen';controls();}); penButton.id='pen-tool'; eraserButton=button('Eraser',()=>{tool='eraser';controls();}); eraserButton.id='pen-eraser'; tools.append(penButton,eraserButton); toolbar.append(tools);
    const colorLabel = element('label','pen-color-label','Ink'); const colorInput = element('select'); colorInput.id='pen-color'; colorInput.setAttribute('aria-label','Ink color');
    for (const [index,name] of ['Graphite','Blue','Berry','Green','Ochre'].entries()) { const option = element('option','',name); option.value=colors[index]; colorInput.append(option); } colorInput.value=color; colorInput.onchange=()=>{color=colorInput.value;}; colorLabel.append(colorInput); toolbar.append(colorLabel);
    const widthLabel = element('label','pen-width-label','Width'); const widthInput = element('input'); widthInput.id='pen-width'; widthInput.type='range'; widthInput.min='1'; widthInput.max='10'; widthInput.step='1'; widthInput.value=String(width); widthInput.setAttribute('aria-label','Pen width'); const widthOutput=element('output','',String(width)); widthOutput.htmlFor='pen-width'; widthInput.oninput=()=>{width=Number(widthInput.value);widthOutput.textContent=String(width);}; widthLabel.append(widthInput,widthOutput); toolbar.append(widthLabel);
    const historyTools=element('div','pen-tool-group'); undoButton=button('Undo',undo);undoButton.id='pen-undo';redoButton=button('Redo',redo);redoButton.id='pen-redo';clearButton=button('Clear',clearDrawing);clearButton.id='pen-clear';historyTools.append(undoButton,redoButton,clearButton);toolbar.append(historyTools);
    const palm=element('label','pen-palm-toggle'),palmInput=element('input');palmInput.type='checkbox';palmInput.id='pen-only';palmInput.checked=doc.penOnly;palmInput.onchange=()=>{doc.penOnly=palmInput.checked;if(currentStroke&&activePointerType==='touch')cancelStroke();persist();controls();};palm.append(palmInput,element('span','','Ignore fingers on canvas'));toolbar.append(palm);toolBox.append(toolbar);
    const body=element('div','pen-body'),drawing=element('section','pen-drawing');drawingSection=drawing;drawing.setAttribute('aria-label','Scratchpad drawing');const paper=element('div','pen-paper');paperNode=paper;
    canvas=element('canvas');canvas.id='pen-canvas';canvas.width=WIDTH;canvas.height=HEIGHT;canvas.tabIndex=0;canvas.setAttribute('role','img');canvas.setAttribute('aria-describedby','pen-canvas-help');context=canvas.getContext('2d');
    emptyHint=element('div','pen-empty');emptyHint.append(element('strong','','Room for your next idea'),element('span','','Draw with your pen. Your strokes save here.'));paper.append(canvas,emptyHint);drawing.append(paper);
    const help=element('p','help','Pressure changes ink width when your pen supports it. Ignore fingers keeps palm marks off this canvas.');help.id='pen-canvas-help';drawing.append(help);body.append(drawing);
    const notes=element('section','pen-notes');notesSection=notes;notes.setAttribute('aria-label','Text notes');const notesHeading=element('div','pen-notes-heading');notesHeading.append(element('label','','Notes'));noteField=element('textarea');noteField.id='pen-notes';noteField.maxLength=MAX_NOTE;noteField.value=doc.notes;noteField.placeholder='An idea, a script, or the next step…';noteField.setAttribute('aria-label','Notes');noteField.disabled=!!loadError;notesHeading.firstChild.htmlFor='pen-notes';
    const handwritingButton=button('Handwrite text',()=>requestHandwriting(noteField));handwritingButton.dataset.penHandwrite='notes';handwritingButton.id='pen-handwriting';handwritingButton.disabled=!!loadError;notesHeading.append(handwritingButton);notes.append(notesHeading,noteField);
    noteField.oninput=()=>{doc.notes=noteField.value.slice(0,MAX_NOTE);scheduleSave();};const noteActions=element('div','pen-note-actions');noteActions.append(button('Copy notes',exportNotes),element('span','help','Up to 8,000 characters'));notes.append(noteActions,element('p','help',isNative()?(isIOS()?'Handwriting uses iPadOS Scribble and its keyboard.':'Handwriting uses your installed Android keyboard.')+' Your keyboard provider’s privacy settings apply. Finished notes sync to your paired PC.':'On a tablet, Handwrite text uses its keyboard or Scribble. Here, type or paste. Notes sync only to your paired PC.'));body.append(notes);host.append(body);
    canvas.addEventListener('pointerdown',beginStroke);canvas.addEventListener('pointermove',moveStroke);canvas.addEventListener('pointerup',finishStroke);canvas.addEventListener('pointercancel',event=>{if(event.pointerId===activePointer)cancelStroke();});canvas.addEventListener('lostpointercapture',event=>{if(event.pointerId===activePointer)cancelStroke();});canvas.addEventListener('contextmenu',event=>event.preventDefault());
    canvas.addEventListener('pointerdown',()=>{if(fullscreenTools)setFullscreenTools(false);});
    canvas.addEventListener('keydown',event=>{if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='z'){event.preventDefault();event.shiftKey?redo():undo();}else if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='y'){event.preventDefault();redo();}});
    if (typeof ResizeObserver !== 'undefined') { resizeObserver?.disconnect();resizeObserver=new ResizeObserver(resize);resizeObserver.observe(canvas); }
    controls();
  }
  function setActive(value) {
    const next = value === true || value === 'pen';
    if (next) { load(); active=true; suspended=false; mount(); host.hidden=false; document.body.classList.add('pen-mode'); resize();if(typeof api==='function')setSyncState('checking');queueSync(0); }
    else { syncEpoch++;clearTimeout(syncPollTimer);syncPollTimer=null;cancelStroke();active=false;setFullscreen(false);persist();if(host)host.hidden=true;document.body.classList.remove('pen-mode'); }
    return true;
  }
  function suspend() { suspended=true;syncEpoch++;clearTimeout(syncPollTimer);syncPollTimer=null;cancelStroke();setFullscreen(false);persist();penPointers.clear();rejectedTouches.clear();penNearbyUntil=0; }
  function resume() { suspended=false;if(active){resize();controls();queueSync(0);} }
  function blurInput() {cancelStroke();setFullscreen(false);persist();penPointers.clear();rejectedTouches.clear();penNearbyUntil=0;}
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
    document.addEventListener('visibilitychange',()=>document.hidden?suspend():resume());window.addEventListener('pagehide',suspend);window.addEventListener('blur',blurInput);window.addEventListener('focus',resume);window.addEventListener('resize',resize);
    window.addEventListener('beforeunload',event=>{if(saveError){event.preventDefault();event.returnValue='';}});
    document.addEventListener('keydown',event=>{if(event.key==='Escape'&&fullscreen){event.preventDefault();if(fullscreenTools)setFullscreenTools(false);else setFullscreen(false);}},{capture:true});
    document.addEventListener('fullscreenchange',()=>{if(browserFullscreen&&document.fullscreenElement!==host)setFullscreen(false);else if(document.fullscreenElement===host)browserFullscreen=true;});
    document.addEventListener('deskwing-fullscreen',event=>{
      // Request echoes can arrive after a newer workspace enters immersive mode.
      // Our own requests already update CSS; only host-driven exits should change it here.
      if(event.detail?.enabled===false&&event.detail.reason!=='request'&&(fullscreen||nativeFullscreen))setFullscreen(false,{nativeEvent:true});
    });
    const priorSuspend=window.monitorSuspend,priorResume=window.monitorResume;
    window.monitorSuspend=(...args)=>{suspend();return priorSuspend?.apply(window,args);};window.monitorResume=(...args)=>{const result=priorResume?.apply(window,args);resume();return result;};
    if (isNative()) {
      handwriteFieldButton=button('Handwrite',()=>requestHandwriting(focusTarget),'quiet pen-field-handwrite');handwriteFieldButton.hidden=true;handwriteFieldButton.dataset.penHandwrite='field';handwriteFieldButton.setAttribute('aria-label','Handwrite in the selected text field');document.body.append(handwriteFieldButton);
      handwriteFieldButton.addEventListener('pointerdown',event=>event.preventDefault());
      document.addEventListener('focusin',event=>{if(event.target===handwriteFieldButton)return;focusTarget=writable(event.target)&&event.target!==noteField?event.target:null;handwriteFieldButton.hidden=!focusTarget;});
      document.addEventListener('pointerdown',event=>{if(focusTarget&&event.target!==handwriteFieldButton&&!event.target.closest('input,textarea')){focusTarget=null;handwriteFieldButton.hidden=true;}},{passive:true});
    }
  }
  window.DeskPen=Object.freeze({setActive,isActive:()=>active,suspend,resume,requestHandwriting,setFullscreen,isFullscreen:()=>fullscreen,setFullscreenTools,hasFullscreenTools:()=>fullscreenTools,resize,syncNow:syncShared,chooseRemote,chooseLocal,restoreRecovery});
  window.setPenMode=async value=>setActive(value);
  document.addEventListener('DOMContentLoaded',initialize,{once:true});
})();
