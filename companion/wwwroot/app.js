'use strict';
const $=s=>document.querySelector(s);
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const paths={
 obs:'M12,24C5.383,24,0,18.617,0,12S5.383,0,12,0s12,5.383,12,12S18.617,24,12,24z M12,1.109 C5.995,1.109,1.11,5.995,1.11,12C1.11,18.005,5.995,22.89,12,22.89S22.89,18.005,22.89,12C22.89,5.995,18.005,1.109,12,1.109z M6.182,5.99c0.352-1.698,1.503-3.229,3.05-3.996c-0.269,0.273-0.595,0.483-0.844,0.78c-1.02,1.1-1.48,2.692-1.199,4.156 c0.355,2.235,2.455,4.06,4.732,4.028c1.765,0.079,3.485-0.937,4.348-2.468c1.848,0.063,3.645,1.017,4.7,2.548 c0.54,0.799,0.962,1.736,0.991,2.711c-0.342-1.295-1.202-2.446-2.375-3.095c-1.135-0.639-2.529-0.802-3.772-0.425 c-1.56,0.448-2.849,1.723-3.293,3.293c-0.377,1.25-0.216,2.628,0.377,3.772c-0.825,1.429-2.315,2.449-3.932,2.756 c-1.244,0.261-2.551,0.059-3.709-0.464c1.036,0.302,2.161,0.355,3.191-0.011c1.381-0.457,2.522-1.567,3.024-2.935 c0.556-1.49,0.345-3.261-0.591-4.54c-0.7-1.007-1.803-1.717-3.002-1.969c-0.38-0.068-0.764-0.098-1.148-0.134 c-0.611-1.231-0.834-2.66-0.528-3.996L6.182,5.99z',
 discord:'M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189Z',

 home:'M3 11 12 3l9 8M5 10v11h5v-7h4v7h5V10',
 homeFilled:'M2 11 12 2l10 9-2 2-1-1v10h-5v-8h-4v8H5V12l-1 1z',
 bed:'M3 5v16M3 17h18v4M3 14h18v3M7 10a3 3 0 1 0 0-6 3 3 0 0 0 0 6M12 8h6a3 3 0 0 1 3 3v3h-9z',
 image:'M3 3h18v18H3zM3 17l6-6 4 4 3-3 5 5M16 7h.1',
 list:'M3 4h3v3H3zM10 5h11M3 11h3v3H3zM10 12h11M3 18h3v3H3zM10 19h11',
 grid:'M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z',
 monitor:'M3 4h18v13H3zM8 21h8M12 17v4',
 gamepad:'M7 7h10c3 0 5 9 4 11-2 2-4-3-6-3H9c-2 0-4 5-6 3C2 16 4 7 7 7zM7 10v4M5 12h4M16 11h.1M19 13h.1',
 chat:'M4 4h16v12H9l-5 5V4',
 coffee:'M3 9h14v9a3 3 0 0 1-3 3H6a3 3 0 0 1-3-3zM17 10h2a3 3 0 0 1 0 6h-2M6 2v3M11 2v3',
 clapper:'M3 8h18v13H3zM3 8l-1-5 18-2 1 5zM7 3l3 4M14 2l3 4M10 12l5 3-5 3z',
 speaker:'M3 9h4l5-5v16l-5-5H3zM16 8a6 6 0 0 1 0 8M19 4a11 11 0 0 1 0 16',
 mic:'M9 4a3 3 0 0 1 6 0v8a3 3 0 0 1-6 0zM5 10v2a7 7 0 0 0 14 0v-2M12 19v3M8 22h8',
 micOff:'M9 7V4a3 3 0 0 1 6 0v8M9 12a3 3 0 0 0 4 3M5 10v2a7 7 0 0 0 11 6M19 10v2M12 19v3M8 22h8M2 2l20 20',
 clap:'M6 19l-3-5a2 2 0 0 1 3-2l2 2 5-8a1.5 1.5 0 0 1 3 1l-3 6 4-5a1.5 1.5 0 0 1 2 2l-3 4 3-2a1.5 1.5 0 0 1 2 2l-5 7c-3 3-7 3-10-2zM5 3l2 3M12 1v3M19 2l-2 3',
 horn:'M3 10h5l12-6v16L8 14H3zM8 14l2 7H6l-1-7M23 9v6',
 stop:'M12 2a10 10 0 1 1 0 20 10 10 0 0 1 0-20M8 8h8v8H8z',
 replay:'M3 10a9 9 0 1 1 1 8M3 4v6h6M10 8l6 4-6 4z',
 aperture:'M12 2a10 10 0 1 1 0 20 10 10 0 0 1 0-20M12 2l5 10-5 10M3 7l11 1 7 9M3 17l5-9 10-4',
 record:'M12 2a10 10 0 1 1 0 20 10 10 0 0 1 0-20M12 7a5 5 0 1 1 0 10 5 5 0 0 1 0-10',
 broadcast:'M12 7a3 3 0 1 1 0 6 3 3 0 0 1 0-6M12 13v9M7 4a8 8 0 0 0 0 13M17 4a8 8 0 0 1 0 13M4 1a12 12 0 0 0 0 19M20 1a12 12 0 0 1 0 19',
 bolt:'M14 2 4 14h7l-1 8 10-13h-7z',
 edit:'M3 21l1-6L16 3a2 2 0 0 1 4 4L8 19zM14 5l5 5',
 settings:'M12 8a4 4 0 1 1 0 8 4 4 0 0 1 0-8M12 2v3M12 19v3M2 12h3M19 12h3M5 5l2 2M17 17l2 2M5 19l2-2M17 7l2-2',
 arrow:'M4 12h16M14 6l6 6-6 6',
 camera:'M3 6h12v13H3zM15 10l6-4v13l-6-4',
 keyboard:'M2 5h20v14H2zM5 9h1M9 9h1M13 9h1M17 9h1M5 13h1M9 13h1M13 13h1M17 13h1M6 16h12',
 link:'M10 14l4-4M8 16l-2 2a4 4 0 0 1-5-5l5-5a4 4 0 0 1 5 0M13 8l2-2a4 4 0 0 1 6 6l-4 4a4 4 0 0 1-5 0',
 filter:'M3 4h18l-7 9v7l-4 2V13z',
 clock:'M12 2a10 10 0 1 1 0 20 10 10 0 0 1 0-20M12 6v6l4 2',
 expand:'M8 3H3v5M16 3h5v5M3 16v5h5M21 16v5h-5',
 collapse:'M3 8h5V3M21 8h-5V3M8 21v-5H3M16 21v-5h5'
};
paths.star='m12 2 3.1 6.3 6.9 1-5 4.9 1.2 6.8-6.2-3.2L5.8 21 7 14.2 2 9.3l6.9-1z';
function icon(name){const filled=['obs','discord','homeFilled'].includes(name);return `<svg viewBox="-2 -2 28 28" aria-hidden="true"${filled?' class="filled-icon"':''}><path d="${paths[name]||paths.grid}"/></svg>`;}
function actionIcon(a){return typeof a.iconImage==='string'&&a.iconImage.startsWith('data:image/png;base64,')&&a.iconImage.length<=100000?`<img class="custom-key-icon" src="${esc(a.iconImage)}" alt="">`:icon(a.icon);}
const native=typeof window.DeckNative!=='undefined';
const desktopConsole=document.body.dataset.surface==='desktop';
const preview=new URLSearchParams(location.search).get('preview')==='1'&&!native;
let adminToken=new URLSearchParams(location.hash.slice(1)).get('admin')||sessionStorage.getItem('deckAdmin')||'';
if(adminToken){sessionStorage.setItem('deckAdmin',adminToken);history.replaceState(null,'',location.pathname+location.search);}
let nextRequest=0;const pending=new Map();
let observedPairingId=null,workspaceConnectionEpoch=0;
function workspaceDocumentApiPath(path){return typeof path==='string'&&/^(?:\/api\/notebooks(?:\/|\?|$)|\/api\/ink(?:\?|$))/.test(path);}
function validPairingId(value){return typeof value==='string'&&/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value);}
function workspaceContextEvent(contextId,reason){
  workspaceContext=null;
  workspaceConnectionEpoch++;
  if(typeof CustomEvent==='function'&&typeof window.dispatchEvent==='function')window.dispatchEvent(new CustomEvent('deskwing-workspace-contextchange',{detail:{contextId,reason}}));
}
function observeNativeConnection(next,reason='connection'){
  if(!next||typeof next.paired!=='boolean')return;
  const identity=next.paired&&validPairingId(next.pairingId)?next.pairingId:'';
  if(observedPairingId!==null&&observedPairingId!==identity)workspaceContextEvent(identity,reason);
  observedPairingId=identity;
}
window.addEventListener('deskwing-connection-changed',()=>{
  // Native changes may commit before this event reaches JavaScript. The envelope
  // binding below is the authority; this notification only retires old UI work.
  observedPairingId=null;workspaceConnectionEpoch++;
  workspaceContext=null;
});
window.nativeResult=(id,result)=>{
  const p=pending.get(id);if(!p)return;clearTimeout(p.timer);pending.delete(id);
  if(p.expectedPairingId&&(p.contextEpoch!==workspaceConnectionEpoch||observedPairingId!==null&&observedPairingId!==p.expectedPairingId)){p.reject(new Error('The PC connection changed. Reopen this document before continuing.'));return;}
  if(!result.error&&/^\/native\/(?:connection|pair|transport|forget)$/.test(p.path))observeNativeConnection(result,p.path.slice(8));
  result.error?p.reject(new Error(result.error)):p.resolve(result);
};
function nativeRequest(path,body,method='POST',timeoutMs=65000,expectedPairingId){
  if(workspaceDocumentApiPath(path)&&!validPairingId(expectedPairingId))return Promise.reject(new Error('Open this document from its original PC before continuing.'));
  return new Promise((resolve,reject)=>{const id=++nextRequest;const timer=timeoutMs>0?setTimeout(()=>{pending.delete(id);reject(new Error('The PC did not respond. Check the connection before trying again.'));},timeoutMs):null;pending.set(id,{resolve,reject,timer,path,expectedPairingId,contextEpoch:workspaceConnectionEpoch});const request={id,path,body,method};if(expectedPairingId!==undefined)request.expectedPairingId=expectedPairingId;window.DeckNative.dispatch(JSON.stringify(request));});
}
async function api(path,body,timeoutMs=65000){
  if(native)return nativeRequest(path,body,body===undefined?'GET':'POST',timeoutMs);
  const response=await fetch(path,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json','X-Deck-Admin':adminToken},body:body===undefined?undefined:JSON.stringify(body),signal:timeoutMs>0?AbortSignal.timeout(timeoutMs):undefined});
  let data;try{data=await response.json();}catch{throw new Error(`Request failed (${response.status}). Reopen the companion to unlock the PC editor.`);}
  if(!response.ok)throw new Error(data.error||`Request failed (${response.status}).`);return data;
}
async function captureWorkspaceContext(){
  if(!native)return 'web:'+String(location.origin||'local');
  const epoch=workspaceConnectionEpoch,next=await nativeRequest('/native/connection',{});
  if(epoch!==workspaceConnectionEpoch||!next.paired||!validPairingId(next.pairingId))throw new Error('The PC connection changed. Reopen this document before continuing.');
  return next.pairingId;
}
async function apiWithWorkspaceContext(path,body,expectedPairingId,timeoutMs=65000){
  if(!workspaceDocumentApiPath(path))throw new Error('This context is only for ink and notebook requests.');
  if(native){
    if(!validPairingId(expectedPairingId)||observedPairingId!==null&&observedPairingId!==expectedPairingId)throw new Error('The PC connection changed. Reopen this document before continuing.');
    // Never replace the caller's retained identity with the newest connection.
    // Native code verifies this envelope before any HTTP headers or body bytes.
    return nativeRequest(path,body,body===undefined?'GET':'POST',timeoutMs,expectedPairingId);
  }
  if(expectedPairingId!=='web:'+String(location.origin||'local'))throw new Error('Reopen this document in this workspace before continuing.');
  return api(path,body,timeoutMs);
}
let boardSignature='',boardRevision=0,lastGrid='',pages=[],pageId='',slice=0,state={},connection={},busy=false,refreshing=false,lastBoard=0,editing=false,dirty=false,toastTimer;
let activeProfileId='',activeProfileName='Default';
let workspaceContext=null;
function workspaceSoundRoute(page){return ({speakers:'Speakers',mic:'Microphone',both:'Speakers and microphone'})[page?.liveSoundpadOutput]||'Output unavailable';}
function acceptWorkspaceContext(board){
  const value=board?.workspace;
  const next=value&&typeof value.computerName==='string'&&typeof value.connectionCode==='string'?{computerName:value.computerName.slice(0,64),connectionCode:value.connectionCode.slice(0,16),version:String(value.version||'').slice(0,32)}:null;
  if(workspaceContext&&next&&workspaceContext.connectionCode!==next.connectionCode)workspaceContextEvent(observedPairingId||'','pc-identity');
  workspaceContext=next;
}
function renderWorkspaceStatus(){
  if(desktopConsole||preview||typeof document.createElement!=='function')return;
  const header=document.querySelector('.topbar');if(!header||typeof header.insertBefore!=='function')return;
  let button=document.getElementById('workspace-status');
  if(!button){button=document.createElement('button');button.type='button';button.id='workspace-status';button.className='quiet workspace-identity';button.onclick=showWorkspaceCheck;const name=document.createElement('strong');name.id='workspace-status-name';const detail=document.createElement('span');detail.id='workspace-status-detail';button.append(name,detail);header.insertBefore(button,document.getElementById('transport'));}
  const connected=state.pcConnected===true;
  document.getElementById('workspace-status-name').textContent=connected?(workspaceContext?.computerName||'Connected PC'):'PC disconnected';
  document.getElementById('workspace-status-detail').textContent='Board · '+activeProfileName+(activeProfileId?' · '+activeProfileId.slice(0,6):'');
  button.setAttribute('aria-label',(connected?'Connected to '+(workspaceContext?.computerName||'PC'):'PC disconnected')+' · Board '+activeProfileName+'. Check workspace and sound output');
  button.title='Check this PC, board, and Soundpad output';button.classList.toggle('is-offline',!connected);
}
async function showWorkspaceCheck(){
  if(preview)return;
  openPanel('Check this workspace','<p id="workspace-check-loading" role="status">Checking this PC, board and sound output…</p>');
  const generation=panelGeneration,owns=()=>panel.open&&panelGeneration===generation;
  try{
    const [board,next,link]=await Promise.all([api('/api/board',undefined,10000),api('/api/state',undefined,10000),native?nativeRequest('/native/connection',{}):Promise.resolve(null)]);
    if(!owns())return;
    acceptWorkspaceContext(board);if(link)connection=link;
    const identity=workspaceContext,profile=String(board.profileName||'Current board'),live=board.pages.filter(page=>page.liveSoundpad);
    openPanel('Check this workspace',`<section class="workspace-check"><p class="help">These checks read status. They do not run board buttons, play sounds, or change your board.</p><dl><dt>PC</dt><dd>${esc(identity?.computerName||'Connected companion')}${identity?.connectionCode?' · '+esc(identity.connectionCode):''}</dd><dt>Board</dt><dd>${esc(profile)} · ${board.pages.length} pages</dd><dt>Connection</dt><dd>${esc(native?(connection.mode==='usb'?'USB':'Wi-Fi')+(connection.fastConnected?' · fast control connected':' · HTTPS fallback'):'Local Windows companion')}</dd><dt>OBS</dt><dd>${next.obsConnected?'Connected':'Not connected. Open OBS setup on the PC if this board uses OBS.'}</dd><dt>Soundpad</dt><dd>${live.length?live.map(page=>esc(page.name)+' · '+page.buttons.length+' sounds · '+esc(workspaceSoundRoute(page))+(page.soundpadWarning?' · '+esc(page.soundpadWarning):'')).join('<br>'):'No live Soundpad page in this board. Add one through the PC board editor if you need it.'}</dd></dl><p>If a page is missing, check the board name first. Use Profiles to review another board; your current pages stay saved.</p><div class="row"><button id="workspace-check-again" class="quiet">Check again</button><button id="workspace-check-privacy" class="quiet">Privacy &amp; support</button>${native?'<button id="workspace-check-connect" class="quiet">Connection settings</button>':''}</div>${live.length?'<p>Microphone-only playback is sent to calls or recordings and may be silent on your speakers. Choose the intended output in the PC page settings.</p>':''}</section>`);
    $('#workspace-check-again').onclick=showWorkspaceCheck;if($('#workspace-check-connect'))$('#workspace-check-connect').onclick=connectionForm;
    const privacy=$('#workspace-check-privacy');if(privacy){privacy.hidden=typeof showPrivacySupport!=='function';if(!privacy.hidden)privacy.onclick=showPrivacySupport;}
    renderWorkspaceStatus();
  }catch(error){if(owns())$('#workspace-check-loading').textContent=error.message+' Open connection settings to reconnect, then check again.';}
}
let executionStatus=null,executionPending='',executionReachable=true,cancelPending=false;
function acceptExecution(next,fresh=false){
  if(!next||typeof next.session!=='string'||!Number.isFinite(next.revision))return;
  if(executionStatus?.session===next.session&&next.revision<executionStatus.revision)return;
  if(fresh||executionStatus?.session!==next.session||next.revision>executionStatus.revision)executionReachable=true;
  executionStatus=next;renderExecution();
}
function renderExecution(){
  const active=executionStatus?.active,visible=!!active||busy;
  $('#execution').hidden=!visible;document.body.classList.toggle('execution-visible',visible);
  $('#execution-label').textContent=active?.label||executionPending||'Running action';
  $('#execution-step').textContent=!executionReachable?'Connection lost. The action may still be running.':active?.cancelling?'Cancelling after the current step. Completed steps are kept.':active?`Step ${active.step} of ${active.total} · ${active.stepLabel}`:'Waiting for the PC…';
  $('#cancel-action').disabled=!active||active.cancelling||cancelPending;
  $('#cancel-action').textContent=active?.cancelling||cancelPending?'Cancelling…':'Cancel action';
}
$('#cancel-action').onclick=async()=>{
  const id=executionStatus?.active?.id;if(!id||cancelPending)return;
  cancelPending=true;renderExecution();
  try{const result=await api('/api/cancel',{id});notice(result.cancelled?'Cancellation requested. Completed steps are kept.':'That action has already finished.');await pollExecutionOnce();}
  catch(error){notice(error.message,true);}finally{cancelPending=false;renderExecution();}
};
async function pollExecutionOnce(){
  try{acceptExecution(await api('/api/execution'),true);}catch{executionReachable=false;renderExecution();}
}
async function watchExecution(){
  if(!preview&&!document.hidden&&(!native||connection.paired))await pollExecutionOnce();
  setTimeout(watchExecution,busy||executionStatus?.active?500:2000);
}
let padFull=false,padSize=24;
try{const saved=JSON.parse(localStorage.getItem('deckPadView')||'{}');padFull=saved.full===true;padSize=saved.size===40?40:24;}catch{}
function boardCapacity(){return padFull?padSize:12;}
function boardColumns(){return getComputedStyle($('#keys')).gridTemplateColumns.split(/\s+/).length;}
function applyPadView(){
  document.body.classList.toggle('full-pad',padFull);document.body.classList.toggle('pad-40',padFull&&padSize===40);
  const toggle=$('#pad-toggle');toggle.setAttribute('aria-pressed',String(padFull));toggle.setAttribute('aria-label',padFull?'Exit full pad':'Full pad');
  toggle.innerHTML=DOMPurify.sanitize(icon(padFull?'collapse':'expand')+'<span>'+(padFull?'Exit full pad':'Full pad')+'</span>');
  $('#pad-density').hidden=!padFull;$('#pad-density').value=String(padSize);
}
function changePadView(full,size=padSize){
  if(typeof hasActiveHold==='function'&&hasActiveHold()){applyPadView();notice('Release the held key before changing the layout.');return;}
  if(typeof arranging!=='undefined'&&arranging){applyPadView();notice('Save or cancel your button order before changing the layout.');return;}
  const first=slice*boardCapacity();padFull=full;padSize=size===40?40:24;slice=Math.floor(first/boardCapacity());
  try{localStorage.setItem('deckPadView',JSON.stringify({full:padFull,size:padSize}));}catch{}
  applyPadView();lastGrid='';renderBoard();
}
function notice(message,error=false){const n=$('#notice');n.textContent=message;n.classList.toggle('error',error);n.hidden=false;clearTimeout(toastTimer);toastTimer=setTimeout(()=>n.hidden=true,error?12000:3000);}
$('#notice').onclick=()=>$('#notice').hidden=true;
const panel=$('#panel');
let panelGeneration=0;
function openPanel(title,html){panelGeneration++;$('#panel-title').textContent=title;$('#panel-body').innerHTML=DOMPurify.sanitize(html);if(!panel.open){if(desktopConsole)panel.show();else panel.showModal();}if(typeof consoleViewChanged==='function')consoleViewChanged('panel',title);if(typeof refreshMonitorActivity==='function')queueMicrotask(refreshMonitorActivity);if(typeof renewProfileLock==='function')queueMicrotask(renewProfileLock);}
async function closePanel(){if(typeof DeckEditor!=='undefined'&&DeckEditor.isOpen()&&DeckEditor.isSaving()){notice('Wait for the board save or transfer to finish.',true);return;}if(dirty&&!await confirmAction('Discard changes?','Your unsaved changes will be lost.'))return;if(typeof DeckEditor!=='undefined'&&DeckEditor.isOpen()&&dirty)DeckEditor.discardDraft();dirty=false;editing=false;panel.close();}
$('#close-panel').onclick=closePanel;
panel.addEventListener('cancel',event=>{if(dirty||typeof DeckEditor!=='undefined'&&DeckEditor.isOpen()&&DeckEditor.isSaving()){event.preventDefault();closePanel();}else editing=false;});
function confirmAction(title,copy){const d=$('#confirm-dialog');$('#confirm-title').textContent=title;$('#confirm-copy').textContent=copy;d.showModal();if(typeof refreshMonitorActivity==='function')refreshMonitorActivity();return new Promise(resolve=>d.addEventListener('close',()=>{if(typeof refreshMonitorActivity==='function')refreshMonitorActivity();resolve(d.returnValue==='confirm');},{once:true}));}
let liveCatalogRevision=null;
function syncSceneCatalog(){const next=state.sceneCatalogRevision;if(typeof next!=='string'||next===liveCatalogRevision)return;liveCatalogRevision=next;if(!pages.some(p=>p.liveScenes))return;lastBoard=0;if(!refreshing&&!panel.open&&!document.hidden&&!(typeof arranging!=='undefined'&&arranging))queueMicrotask(()=>refresh(true));}
function labelFor(a){if(a.kind==='stream')return !state.obsConnected?'Stream control':state.streaming?'Stop stream':'Start stream';if(a.kind==='record')return !state.obsConnected?'Record control':state.recording?'Stop recording':'Record';return a.label;}
function renderBoard(){
  if(desktopConsole){if(typeof renderConsoleSummary==='function')renderConsoleSummary();return;}
  if(typeof hasActiveHold==='function'&&hasActiveHold())return;
  const page=pages.find(p=>p.id===pageId)||pages[0];if(!page)return;pageId=page.id;
  const visible=typeof soundpadBoardButtons==='function'?soundpadBoardButtons(page):page.buttons;
  const focusedSound=typeof rememberSoundpadBoardFocus==='function'?rememberSoundpadBoardFocus(page):null;
  const capacity=boardCapacity(),total=Math.max(1,Math.ceil(visible.length/capacity));if(slice>=total)slice=total-1;
  const keys=$('#keys');keys.replaceChildren();
  for(const a of visible.slice(slice*capacity,slice*capacity+capacity)){
    const button=document.createElement('button');button.className='key '+(['violet','amber','red','blue'].includes(a.color)?a.color:'blue');button.dataset.id=a.id;
    const active=a.kind==='scene'&&state.obsConnected&&a.target===state.scene;
    button.classList.toggle('active',active);button.setAttribute('aria-label',labelFor(a)+(active?', active':''));button.title=labelFor(a);
    button.innerHTML=DOMPurify.sanitize(actionIcon(a)+`<span class="key-label">${esc(labelFor(a))}</span>`+(active?'<span class="active-tag">ACTIVE</span>':a.configured===false?`<small>${page.liveSoundpad?'Unavailable':'Set up on PC'}</small>`:''));
    button.onclick=()=>trigger(a,button);keys.appendChild(button);
  }
  if(visible.length===0){const empty=document.createElement('div');empty.className='empty-key';const soundEmpty=page.liveSoundpad&&typeof soundpadBoardEmpty==='function'?soundpadBoardEmpty(page):null;empty.innerHTML=DOMPurify.sanitize(icon(page.icon)+(soundEmpty?'<strong>'+esc(soundEmpty.title)+'</strong><span>'+esc(soundEmpty.message)+'</span>':page.liveSoundpad?(page.buttons.length?'<strong>No matching sounds</strong><span>Clear the search or choose All categories to see every sound.</span>':'<strong>No live Soundpad sounds available</strong><span>Keep Soundpad running on the PC. This page follows its entire loaded library automatically.</span>'):page.liveScenes?'<strong>No live OBS scenes available</strong><span>Connect OBS in Windows setup. This page follows its current scene collection automatically.</span>':'<strong>No buttons yet</strong><span>Add actions using Edit in the Windows companion.</span>'));keys.appendChild(empty);}
  const pagination=$('#pagination');pagination.hidden=total<=1;pagination.innerHTML=DOMPurify.sanitize(`<button id="prev-keys" class="quiet" ${slice===0?'disabled':''}>Previous</button><span>Page ${slice+1} of ${total}</span><button id="next-keys" class="quiet" ${slice===total-1?'disabled':''}>Next</button>`);
  $('#prev-keys').onclick=()=>{slice--;renderBoard();};$('#next-keys').onclick=()=>{slice++;renderBoard();};
  if(typeof soundpadBoardPagination==='function')soundpadBoardPagination(page,total);
  if(typeof restoreSoundpadBoardFocus==='function')restoreSoundpadBoardFocus(focusedSound);
  const nav=$('#pages');nav.replaceChildren();
  const profileButton=document.createElement('button');profileButton.className='page-tab profile-launch';profileButton.id='profiles-button';profileButton.title='Profiles · '+activeProfileName;profileButton.setAttribute('aria-label','Profiles · '+activeProfileName);profileButton.innerHTML=DOMPurify.sanitize(icon('grid')+'<span>Profiles</span>');profileButton.onclick=()=>showProfiles();nav.appendChild(profileButton);
  for(const p of pages){const button=document.createElement('button');button.className='page-tab'+(p.id===pageId?' selected':'');button.setAttribute('aria-current',p.id===pageId?'page':'false');button.innerHTML=DOMPurify.sanitize(icon(p.icon==='home'&&p.id===pageId?'homeFilled':p.icon)+`<span>${esc(p.name)}</span>`);button.onclick=()=>{pageId=p.id;slice=0;renderBoard();};nav.appendChild(button);}
  if(typeof attachBoardReorder==='function')attachBoardReorder();
  if(typeof attachHoldButtons==='function')attachHoldButtons();
}
async function trigger(a,button){
  if(preview){notice('Design preview — pair a tablet to use real controls.');return;}
  if(busy){notice('An action is still running.');return;}
  if(a.configured===false){notice(a.id.startsWith('live-soundpad:')?'This sound has no unique file identity in Soundpad. Check duplicated or missing entries, then refresh.':'Set this button’s file, app, or shortcut using Edit on the PC.',true);return;}
  let confirmed=false;if(a.confirm){confirmed=await confirmAction(labelFor(a)+'?',a.kind==='stream'?'This changes the stream broadcast in OBS.':'Run this action on your streaming PC?');if(!confirmed)return;}
  busy=true;button.disabled=true;button.classList.add('pressed');
  executionPending=labelFor(a);renderExecution();
  try{const running=api('/api/action',{id:a.id,confirmed});pollExecutionOnce();await running;notice(labelFor(a)+' — done');if(typeof afterControl==='function')afterControl();else await refresh(true);}catch(error){notice(error.message,true);}finally{busy=false;executionPending='';button.disabled=false;button.classList.remove('pressed');await pollExecutionOnce();renderExecution();}
}
function buildMixer(){
  $('#channels').innerHTML=DOMPurify.sanitize(['mic','desktop'].map(ch=>`<section class="channel" data-channel="${ch}"><div class="channel-title">${icon(ch==='mic'?'mic':'monitor')}<span>${ch==='mic'?'Mic':'Desktop'}</span></div><output class="db" id="db-${ch}">— dB</output><div class="slider-wrap"><input class="fader" id="fader-${ch}" type="range" min="-60" max="0" step="1" value="-12" aria-label="${ch==='mic'?'Microphone':'Desktop audio'} volume in decibels" disabled><div class="scale" aria-hidden="true"><span>0</span><span>−12</span><span>−24</span><span>−36</span><span>−48</span><span>−60</span></div></div><span class="input-error" id="input-error-${ch}" hidden>Set input on PC</span><button class="mute" id="mute-${ch}" disabled>${icon('micOff')}<span>Mute</span></button></section>`).join(''));
  for(const ch of ['mic','desktop']){
    const fader=$('#fader-'+ch);fader.addEventListener('input',()=>{$('#db-'+ch).textContent=fader.value+' dB';fader.style.setProperty('--level',((Number(fader.value)+60)/60*100)+'%');fader.dataset.dragging='true';if(!preview&&typeof queueDeckVolume==='function')queueDeckVolume(ch,Number(fader.value)).catch(()=>{});});
    fader.addEventListener('change',async()=>{if(preview){delete fader.dataset.dragging;return;}try{if(typeof queueDeckVolume==='function')await queueDeckVolume(ch,Number(fader.value));else await api('/api/audio',{channel:ch,db:Number(fader.value)});}catch(error){notice(error.message,true);}finally{delete fader.dataset.dragging;if(typeof afterControl==='function')afterControl();else await refresh(true);}});
    $('#mute-'+ch).onclick=async()=>{if(preview)return;try{await api('/api/mute',{channel:ch});if(typeof afterControl==='function')afterControl();else await refresh(true);}catch(error){notice(error.message,true);}};
  }
}
function updateState(){
  renderWorkspaceStatus();
  if(typeof syncSceneCatalog==='function')syncSceneCatalog();
  if(state.profileId&&activeProfileId!==state.profileId)lastBoard=0;
  if(state.execution)acceptExecution(state.execution);
  const computerSurface=document.body.classList.contains('computer-mode');
  $('#obs-dot').classList.toggle('connected',computerSurface?state.pcConnected===true:!!state.obsConnected);$('#obs-status').textContent=computerSurface?(state.pcConnected===false?'PC disconnected':state.pcConnected===true?'PC connected':'Connecting to PC'):state.pcConnected===false?'PC disconnected':state.obsConnected?'OBS connected':'OBS disconnected';
  $('#connection').title=computerSurface?(state.pcConnected===false?state.error||'Reconnect the PC':'Connection settings'):state.error||'Connection settings';$('#transport').textContent=preview?'Preview':native?(connection.mode==='usb'?'USB':'Wi-Fi'):'PC editor';
  $('#stream-status').innerHTML=DOMPurify.sanitize(`<span class="dot ${state.obsConnected&&state.streaming?'live':''}"></span>${!state.obsConnected?'Stream unknown':state.streaming?'Streaming live':state.recording?'Recording':'Stream offline'}`);
  if(desktopConsole){if(typeof renderConsoleSummary==='function')renderConsoleSummary();return;}
  for(const ch of ['mic','desktop']){
    const input=state.inputs?.[ch];const available=!!(state.obsConnected&&input?.available);const fader=$('#fader-'+ch);fader.disabled=!available;$('#mute-'+ch).disabled=!available;
    if(!fader.dataset.dragging){const db=Number.isFinite(input?.db)?Math.max(-60,Math.min(0,input.db)):-60;fader.value=String(Math.round(db));fader.style.setProperty('--level',((db+60)/60*100)+'%');$('#db-'+ch).textContent=available?Math.round(db)+' dB':'— dB';}
    $('#input-error-'+ch).hidden=available||!state.obsConnected;$('#input-error-'+ch).title=input?.error||'';
    const mute=$('#mute-'+ch);mute.classList.toggle('is-muted',!!input?.muted);mute.setAttribute('aria-pressed',String(!!input?.muted));mute.innerHTML=DOMPurify.sanitize(icon(input?.muted?'micOff':ch==='mic'?'mic':'speaker')+`<span>${input?.muted?'Unmute':'Mute'}</span>`);
  }
  if(typeof arranging==='undefined'||!arranging){
    const grid=boardRevision+'|'+pageId+'|'+slice+'|'+boardCapacity();
    if(lastGrid!==grid&&(typeof hasActiveHold!=='function'||!hasActiveHold())){lastGrid=grid;renderBoard();}
    const page=pages.find(page=>page.id===pageId);
    for(const button of $('#keys').querySelectorAll('.key')){
      const action=page?.buttons.find(action=>action.id===button.dataset.id);if(!action)continue;
      const active=action.kind==='scene'&&state.obsConnected&&action.target===state.scene;
      button.classList.toggle('active',!!active);button.setAttribute('aria-label',labelFor(action)+(active?', active':''));
      const label=button.querySelector('.key-label');if(label)label.textContent=labelFor(action);
      let tag=button.querySelector('.active-tag');if(active&&!tag){tag=document.createElement('span');tag.className='active-tag';tag.textContent='ACTIVE';button.appendChild(tag);}else if(!active)tag?.remove();
    }
  }
}
async function refresh(force=false){
  if(refreshing||preview||(!force&&(document.hidden||panel.open)))return;refreshing=true;
  try{
    if((typeof arranging==='undefined'||!arranging)&&(force||Date.now()-lastBoard>(pages.some(p=>p.liveSoundpad)?5000:10000))){const board=await api('/api/board');acceptWorkspaceContext(board);if(typeof board.sceneCatalogRevision==='string')liveCatalogRevision=board.sceneCatalogRevision;const signature=JSON.stringify([board.profileId,board.profileName,board.pages]);if(signature!==boardSignature){if(activeProfileId!==board.profileId){pageId='';slice=0;}activeProfileId=board.profileId||'';activeProfileName=board.profileName||'Default';pages=board.pages;boardSignature=signature;boardRevision++;if(typeof hasActiveHold!=='function'||!hasActiveHold())renderBoard();}lastBoard=Date.now();}
    if(force||typeof hasLiveState!=='function'||!hasLiveState()){const next=await api('/api/state');if(typeof acceptDeckState==='function')acceptDeckState(next);else state=next;}state.pcConnected=true;updateState();
  }catch(error){state={pcConnected:false,obsConnected:false,inputs:{},error:error.message};updateState();if(force)notice(error.message,true);}
  finally{refreshing=false;}
}
async function connectionForm(){
  window.deckPairPorts=null;
  if(native){try{connection=await nativeRequest('/native/connection',{});}catch{}}
  openPanel('Connect your tablet',`<section class="setup"><h3>Your studio, one tap away.</h3><p class="help">${connection.fastConnected?'Persistent encrypted control link connected'+(connection.roundTripMs>=0?' · last network round-trip '+Number(connection.roundTripMs).toFixed(1)+' ms':''):'HTTPS fallback · fast control uses TCP 8744; USB needs both tunnel ports'}</p><p>Run DeskWing-Companion.exe on your Windows PC, then choose <strong>Pair tablet</strong>. Enter the address, certificate code, and pairing PIN shown there.</p><div class="form-grid"><label>Connection<select id="pair-mode"><option value="wifi">Wi-Fi — same local network</option><option value="usb">USB — connected with USB-Connect.cmd</option></select></label><label>PC Wi-Fi address<input id="pair-host" placeholder="192.168.1.100" inputmode="decimal" value="${esc(connection.host||'')}"></label><label class="wide">Certificate code from your PC<input id="pair-cert" placeholder="6 groups of 4 letters and numbers" autocomplete="off" autocapitalize="characters" spellcheck="false"></label><label>Six-digit pairing PIN<input id="pair-code" placeholder="123456" inputmode="numeric" maxlength="6" autocomplete="off"></label></div><p class="help">Wi-Fi uses encrypted local-network control. For USB, enable USB debugging on the tablet and run USB-Connect.cmd in the Windows package. Keep the companion running.</p><div class="row"><button id="pair-submit" class="primary">Pair and connect</button>${connection.paired?'<button id="forget-pc" class="danger">Forget this PC</button>':''}</div>${connection.paired?'<div class="row"><button id="switch-transport" class="quiet">Switch to '+(connection.mode==='usb'?'Wi-Fi':'USB')+'</button></div>':''}<p id="pair-error" role="alert" class="help"></p></section>`);
  $('#pair-mode').value=connection.mode||'wifi';$('#pair-mode').onchange=()=>$('#pair-host').disabled=$('#pair-mode').value==='usb'&&!connection.paired;$('#pair-mode').onchange();
  $('#pair-submit').onclick=async()=>{const button=$('#pair-submit');button.disabled=true;$('#pair-error').textContent='Connecting securely…';try{connection=await nativeRequest('/native/pair',{host:$('#pair-host').value.trim(),mode:$('#pair-mode').value,certificate:$('#pair-cert').value.trim(),code:$('#pair-code').value.trim(),...(window.deckPairPorts||{})});panel.close();await refresh(true);notice('Tablet paired. Your board is ready.');}catch(error){$('#pair-error').textContent=error.message;}finally{button.disabled=false;}};
  if($('#forget-pc'))$('#forget-pc').onclick=async()=>{if(!await confirmAction('Forget this PC?','You will need a new pairing code to reconnect.'))return;connection=await nativeRequest('/native/forget',{});connectionForm();};
  if($('#switch-transport'))$('#switch-transport').onclick=async()=>{try{connection=await nativeRequest('/native/transport',{mode:connection.mode==='usb'?'wifi':'usb',host:$('#pair-host').value.trim()});panel.close();await refresh(true);}catch(error){notice(error.message,true);}};
  if(typeof attachTabletPairing==='function')attachTabletPairing();
}
let config,selectedPage=0,selectedButton=0;
const kinds={scene:'Switch OBS scene',preview:'Set preview scene',transition:'Take studio transition',sound:'Play sound on PC',stopSounds:'Stop all deck sounds',app:'Open Windows app',url:'Open website',hotkey:'Send keyboard shortcut',mute:'Toggle OBS input mute',source:'Toggle source visibility',filter:'Toggle source filter',media:'Restart / stop OBS media',record:'Toggle recording',stream:'Toggle streaming',replay:'Save replay buffer',virtualCamera:'Toggle virtual camera',studioMode:'Toggle studio mode',macro:'Run multiple actions',delay:'Delay (milliseconds)'};
const targetHints={scene:'Exact OBS scene name',preview:'Exact OBS scene name',sound:'C:\\Sounds\\applause.wav',app:'C:\\Program Files\\App\\App.exe or absolute .lnk path',url:'https://example.com',hotkey:'CTRL+SHIFT+M, PLAYPAUSE, or VOLUMEUP',mute:'OBS input name',source:'OBS scene name',filter:'OBS source name',media:'OBS media input name',delay:'0–10000 milliseconds'};
Object.assign(kinds,{soundpadPlay:'Soundpad · play sound',soundpadStop:'Soundpad · stop',soundpadPause:'Soundpad · pause / resume',soundpadNext:'Soundpad · next sound',soundpadPrevious:'Soundpad · previous sound'});
Object.assign(targetHints,{soundpadPlay:'Index from Soundpad All sounds (1, 2, 3, …)'});
const newAction=()=>({id:crypto.randomUUID().replaceAll('-',''),label:'New button',kind:'scene',target:'',value:'',icon:'monitor',color:'violet',confirm:false,steps:[]});
function options(items,value){return Object.entries(items).map(([v,label])=>`<option value="${esc(v)}" ${v===value?'selected':''}>${esc(label)}</option>`).join('');}
async function showEditor(){return DeckEditor.show();}
function renderEditor(){return DeckEditor.render();}
function markDirty(){if(typeof DeckEditor!=='undefined')DeckEditor.changed();else{dirty=true;if($('#save-status'))$('#save-status').textContent='Unsaved changes';}}
async function saveConfig(close){return DeckEditor.save(close);}
function renderActionForm(a){
  if(typeof enhanceActionEditor==='function')queueMicrotask(()=>enhanceActionEditor(a));
  const needsTarget=Object.hasOwn(targetHints,a.kind);const needsValue=['source','filter','media','soundpadPlay','voicemeeter'].includes(a.kind);
  $('#action-form').innerHTML=DOMPurify.sanitize(`<h3>${esc(a.label)}</h3><div class="form-grid"><label>Button label<input id="action-label" value="${esc(a.label)}" maxlength="60"></label><label>Action<select id="action-kind">${options(kinds,a.kind)}</select></label><label>Icon<select id="action-icon">${options(Object.fromEntries(Object.keys(paths).map(x=>[x,x])),a.icon)}</select></label><label>Color<select id="action-color">${options({violet:'Violet · scenes',amber:'Amber · sounds',blue:'Blue · apps',red:'Red · broadcast'},a.color)}</select></label>${needsTarget?`<label class="wide">${a.kind==='delay'?'Delay':'Target'}<input id="action-target" value="${esc(a.target)}" placeholder="${esc(targetHints[a.kind])}"></label>`:''}${needsValue?`<label class="wide">${a.kind==='source'?'Source name':a.kind==='filter'?'Filter name':a.kind==='soundpadPlay'?'Soundpad output (default, both, mic, speakers)':a.kind==='voicemeeter'?'Value: gain in dB or mute 0 / 1':'Media action (restart or stop)'}<input id="action-value" value="${esc(a.value)}" placeholder="${a.kind==='media'?'restart or stop':''}"></label>`:''}</div><div class="row"><label class="check"><input id="action-confirm" type="checkbox" ${a.confirm?'checked':''}>Confirm before running</label></div>${a.kind==='sound'?'<p class="help">Sound plays on the PC’s default output. Enable OBS Desktop Audio capture, or route that output through your mixer, so viewers hear it.</p>':''}${a.kind==='hotkey'?'<p class="help">Shortcuts reach the foreground app or a running app’s configured global keybind. Configure matching shortcuts in Discord or your other tool, then leave its keybind settings before testing.</p>':''}${a.kind==='macro'?'<h3>Actions in order</h3><p class="help">Steps run in order and stop on the first error. Use Delay for timing. Stream and recording steps always require confirmation.</p><div id="macro-steps"></div><button id="add-step" class="quiet">Add step</button>':''}<div class="row"><button id="move-left" class="quiet">Move earlier</button><button id="move-right" class="quiet">Move later</button><button id="delete-button" class="danger">Delete button</button></div>`);
  for(const [id,key] of [['#action-label','label'],['#action-icon','icon'],['#action-color','color'],['#action-target','target'],['#action-value','value']])if($(id))$(id).oninput=e=>{a[key]=e.target.value;if(key==='target'&&a.kind==='soundpadPlay')a.soundpadKey='';markDirty();};
  $('#action-kind').onchange=e=>{a.kind=e.target.value;a.soundpadKey='';if(a.kind==='holdHotkey')a.confirm=false;markDirty();renderActionForm(a);};$('#action-confirm').onchange=e=>{a.confirm=e.target.checked;markDirty();};
  if(a.kind==='holdHotkey'||a.kind==='voicemeeter'){const note=document.createElement('p');note.className='help';note.textContent=a.kind==='holdHotkey'?'Hold shortcuts need a matching push-to-talk binding in the target app. Release, backgrounding, or loss of the renewable three-second lease sends key-up. They cannot be macro steps or require confirmation.':'Voicemeeter must already be running from its standard Program Files installation. Set Strip[0].Gain to -6 for -6 dB, or Bus[0].Mute to 1 to mute (0 to unmute). Indices depend on your edition.';$('#action-form').prepend(note);}
  if(a.kind.startsWith('soundpad')){const note=document.createElement('p');note.className='help';note.textContent='Keep Soundpad running on the PC. Play uses the index in All sounds. Output: mic sends to your microphone, speakers plays locally, both does both, and default uses Soundpad’s play mode. These controls work without focusing Soundpad.';$('#action-form').prepend(note);}
  const page=config.pages[selectedPage];
  function move(offset){const index=selectedButton+offset;if(index<0||index>=page.buttons.length)return;[page.buttons[selectedButton],page.buttons[index]]=[page.buttons[index],page.buttons[selectedButton]];selectedButton=index;dirty=true;renderEditor();}
  $('#move-left').onclick=()=>move(-1);$('#move-right').onclick=()=>move(1);$('#delete-button').onclick=async()=>{if(!await confirmAction('Delete '+a.label+'?','This removes the button when you save.'))return;page.buttons.splice(selectedButton,1);selectedButton=Math.max(0,selectedButton-1);dirty=true;renderEditor();};
  if(a.kind==='macro'){
    if(typeof renderMacroEditor==='function'){renderMacroEditor(a);return;}
    a.steps??=[];$('#macro-steps').innerHTML=DOMPurify.sanitize(a.steps.map((s,i)=>`<div class="step"><div class="form-grid"><label>Action<select data-step="${i}" data-field="kind">${options(Object.fromEntries(Object.entries(kinds).filter(([key])=>key!=='macro'&&key!=='holdHotkey')),s.kind)}</select></label><label>Target / file / milliseconds<input data-step="${i}" data-field="target" value="${esc(s.target)}"></label><label>Source / filter / output<input data-step="${i}" data-field="value" value="${esc(s.value)}"></label></div><div class="row"><button class="quiet" data-remove-step="${i}">Remove step ${i+1}</button></div></div>`).join(''));
    document.querySelectorAll('[data-step]').forEach(el=>el.oninput=e=>{const step=a.steps[Number(e.target.dataset.step)],field=e.target.dataset.field;step[field]=e.target.value;if(field==='kind'||(field==='target'&&step.kind==='soundpadPlay'))step.soundpadKey='';markDirty();});
    document.querySelectorAll('[data-remove-step]').forEach(el=>el.onclick=()=>{a.steps.splice(Number(el.dataset.removeStep),1);markDirty();renderActionForm(a);});
    $('#add-step').onclick=()=>{const step=newAction();step.label='Macro step';a.steps.push(step);markDirty();renderActionForm(a);};
  }
}
async function showPairing(){
  if(native||preview||dirty){notice('Save or discard your changes before pairing a tablet.',true);return;}
  openPanel('Pair an Android tablet','<p role="status">Preparing tablet pairing…</p><p id="pairing-load-error" role="alert"></p>');let generation=panelGeneration;
  const owns=()=>panel.open&&panelGeneration===generation;
  try{const info=await api('/api/admin/info');if(!owns())return;const pairing=await api('/api/admin/pairing',{});if(!owns())return;const grouped=pairing.certificateCode.match(/.{1,4}/g).join('-');
    openPanel('Pair an Android tablet',`<section class="setup"><h3>Connect to this PC</h3><p>Install DeskWing.apk on the tablet. Open it and enter these details. This PIN works once and expires in five minutes.</p><div class="pair-details"><p>PC Wi-Fi address</p><strong class="cert-code">${info.addresses.map(esc).join(' · ')||'No network address found. Use USB.'}</strong><p>Certificate code</p><strong class="cert-code">${esc(grouped)}</strong><p>Pairing PIN</p><strong class="pair-code">${esc(pairing.code)}</strong></div><h3>USB connection</h3><p>Connect a data cable, enable USB debugging on the tablet, approve this PC, and run <strong>USB-Connect.cmd</strong> in the Windows package. Select USB in the tablet pairing screen. After pairing, you can switch between Wi-Fi and USB without pairing again.</p><p class="help">For Wi-Fi, allow the companion through Windows Firewall on your private network. Keep port ${info.httpsPort} on the local network; internet access is not needed.</p><h3>Paired tablets</h3><div id="devices">${info.devices.length?info.devices.map(d=>`<div class="device-row"><span>${esc(d.name)}</span><button class="danger" data-revoke="${esc(d.id)}">Revoke</button></div>`).join(''):'<p>No tablets paired yet.</p>'}</div><div class="row"><button id="new-code" class="quiet">Generate a new PIN</button></div></section>`);
    generation=panelGeneration;
    if(typeof attachPairingQr==='function')attachPairingQr(info);
    $('#new-code').onclick=showPairing;document.querySelectorAll('[data-revoke]').forEach(b=>b.onclick=async()=>{if(!owns()||!await confirmAction('Revoke this tablet?','It will lose access to this PC immediately.')||!owns())return;try{await api('/api/admin/revoke',{id:b.dataset.revoke});if(owns())await showPairing();}catch(error){if(owns())notice(error.message,true);}});
  }catch(error){if(owns())$('#pairing-load-error').textContent=error.message;}
}
function previewData(){
  const names=['Gameplay','Just chatting','Be right back','Starting','Applause','Air horn','Stop sounds','Save replay','Open OBS','Open Discord','Record','Start stream'];
  const kindsList=['scene','scene','scene','scene','sound','sound','stopSounds','replay','app','app','record','stream'];
  const icons=['gamepad','chat','bed','clapper','clap','horn','stop','replay','obs','discord','record','broadcast'];
  const buttons=names.map((label,i)=>({id:String(i),label,kind:kindsList[i],icon:icons[i],color:i<4?'violet':i<8?'amber':i<10?'blue':'red',target:label,configured:true}));
  pages=['Main','Scenes','Sounds','Apps','Macros'].map((name,i)=>({id:String(i),name,icon:['home','image','speaker','grid','list'][i],buttons:i===0?buttons:i===1?buttons.slice(0,4):i===2?buttons.slice(4,7):i===3?buttons.slice(8,10):[]}));
  state={obsConnected:true,scene:'Gameplay',streaming:false,recording:false,inputs:{mic:{available:true,db:-6,muted:false},desktop:{available:true,db:-12,muted:false}}};$('#preview-label').hidden=false;updateState();
}
async function init(){
  if(desktopConsole)return;
  applyPadView();$('#pad-toggle').onclick=()=>changePadView(!padFull);$('#pad-density').onchange=()=>changePadView(padFull,Number($('#pad-density').value));
  buildMixer();$('#edit-button').innerHTML=DOMPurify.sanitize(icon('settings')+'<span>Edit</span>');$('#edit-button').onclick=()=>typeof arranging!=='undefined'&&arranging?saveArrangement():showEditor();
  $('#pair-button').hidden=native||preview;$('#pair-button').onclick=showPairing;
  $('#connection').onclick=()=>native?connectionForm():state.error?notice(state.error,true):notice('The PC editor controls OBS through its local WebSocket server.');
  if(preview){previewData();return;}
  if(native){try{connection=await nativeRequest('/native/connection',{});}catch(error){notice(error.message,true);}if(!connection.paired){$('#obs-status').textContent='Pair your PC';connectionForm();return;}}
  await refresh(true);
}
init().catch(error=>notice(error.message,true));
watchExecution();
setInterval(()=>{if(!native||connection.paired)refresh();},1600);
document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh(true);});
