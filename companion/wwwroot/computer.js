'use strict';
// Contextual controls use saved companion bindings. Choosing a layout never launches an app.
let computerMode='studio',computerSnapshot=null,computerReachable=false,computerReadPending=false,computerChoosing=false,computerEpoch=0,computerSignature='',computerSuspended=false,computerRailScroll=0,computerRailPreset='';
let computerDisplayPanel=null,computerDisplaySnapshot=null,computerDisplayReading=false,computerDisplayWanted=false,computerDisplayEpoch=0,computerDisplayTimer=null,computerDisplayPending=false,computerDisplaySignature='';
let computerDisplayCapabilities=null,computerDisplayRefreshMode='60',computerDisplayNativeSession=null,computerDisplayNativeStarting=false,computerDisplayTransport='';
let computerDisplayQualityPreset='balanced',computerDisplayAdaptiveQuality=true,computerDisplayInputStatus=null,computerDisplayVideoStatus=null;
let computerJog=null;
const computerNode=id=>document.getElementById(id);
function computerText(tag,className,text){const node=document.createElement(tag);if(className)node.className=className;if(text!==undefined)node.textContent=text;return node;}
function computerIcon(kind){
  const paths={desktop:'M3 4h18v12H3z M8 21h8 M12 16v5',youtube:'M9 7l8 5-8 5z',spotify:'M4 8c5-3 11-3 16 0 M6 12c4-2 8-2 12 0 M8 16c3-1 5-1 8 0',coding:'M8 5l-6 7 6 7 M16 5l6 7-6 7 M14 3l-4 18',resolve:'M3 5h18v14H3z M7 9v6 M12 9v6 M17 9v6',photoshop:'M4 3h16v18H4z M8 16V8h4a3 3 0 010 6H8',claude:'M12 3v18 M3 12h18 M6 6l12 12 M6 18L18 6',save:'M4 3h13l3 3v15H4z M8 3v6h8V3 M8 21v-7h8v7',search:'M10 3a7 7 0 110 14 7 7 0 010-14 M15 15l6 6',undo:'M8 5L3 10l5 5 M3 10h11a6 6 0 010 12',redo:'M16 5l5 5-5 5 M21 10H10a6 6 0 000 12',reverse:'M17 4L5 12l12 8z',play:'M7 4l12 8-12 8z',stop:'M5 5h14v14H5z',left:'M5 4v16 M18 5l-9 7 9 7',right:'M19 4v16 M6 5l9 7-9 7',copy:'M8 8h13v13H8z M16 8V3H3v13h5',brush:'M14 3l7 7-10 10-7-7z M4 13l-2 9 9-2',move:'M12 2v20 M2 12h20 M8 6l4-4 4 4 M8 18l4 4 4-4 M6 8l-4 4 4 4 M18 8l4 4-4 4',custom:'M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 17h6 M17 14v6'};
  const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox','0 0 24 24');svg.setAttribute('aria-hidden','true');const path=document.createElementNS('http://www.w3.org/2000/svg','path');path.setAttribute('d',paths[kind]||paths.custom);svg.appendChild(path);return svg;
}
function computerToolIcon(control,preset){
  const key=control.shortcut.toUpperCase(),known={'CTRL+S':'save','CTRL+F':'search','CTRL+Z':'undo','CTRL+Y':'redo','CTRL+SHIFT+Z':'redo','CTRL+C':'copy','LEFT':'left','RIGHT':'right'};
  if(known[key])return computerIcon(known[key]);if(preset.id==='resolve'&&['J','K','L'].includes(key))return computerIcon({J:'reverse',K:'stop',L:'play'}[key]);if(preset.id==='photoshop'&&['B','V','Z'].includes(key))return computerIcon({B:'brush',V:'move',Z:'search'}[key]);if(key==='CTRL+SHIFT+P')return computerIcon('coding');return null;
}
function revealComputerPreset(rail,presetId,force=false){
  rail.scrollLeft=computerRailScroll;
  if(force||presetId!==computerRailPreset){const current=[...rail.children].find(node=>node.getAttribute('aria-pressed')==='true');if(current){const bounds=rail.getBoundingClientRect(),selected=current.getBoundingClientRect();rail.scrollLeft+=selected.left-bounds.left-(bounds.width-selected.width)/2;}computerRailPreset=presetId||'';}
  computerRailScroll=rail.scrollLeft;
}
function computerNavigationAllowed(){
  if(computerSuspended)return false;
  if(computerChoosing||busy||executionStatus?.active||window.MediaTouchBar?.isBusy()||(typeof hasActiveHold==='function'&&hasActiveHold())){notice('Finish the running action and release held keys first.',true);return false;}
  if(typeof arranging!=='undefined'&&arranging){notice('Save or cancel your button order first.',true);return false;}
  return true;
}
function syncComputerConsoleMode(){
  if(typeof desktopConsole==='undefined'||!desktopConsole)return;
  const title=computerNode('console-title');if(title)title.textContent=computerMode==='computer'?(panel.open?computerNode('panel-title').textContent:'Computer Copilot'):'Studio overview';
  for(const button of document.querySelectorAll('.console-nav button'))button.removeAttribute('aria-current');
  computerNode(computerMode==='computer'?'console-computer':'console-home')?.setAttribute('aria-current','page');
}
async function setComputerMode(value){
  const next=value==='computer'?'computer':'studio',selector=computerNode('app-mode');
  if(next===computerMode){if(selector)selector.value=computerMode;return true;}
  if(!computerNavigationAllowed()){if(selector)selector.value=computerMode;return false;}
  if(dirty){if(!await confirmAction('Discard changes?','Your unsaved changes will be lost when you change modes.')){if(selector)selector.value=computerMode;return false;}if(!computerNavigationAllowed()){if(selector)selector.value=computerMode;return false;}if(typeof DeckEditor!=='undefined'&&DeckEditor.isOpen())DeckEditor.discardDraft();dirty=false;}
  editing=false;if(panel.open)panel.close();stopComputerDisplayView();stopComputerJog();if(next==='studio')window.MediaTouchBar?.unmount();
  if(typeof setMonitorOpen==='function'&&typeof monitorOpen!=='undefined'&&monitorOpen)setMonitorOpen(false);
  computerMode=next;computerEpoch++;computerSignature='';document.body.classList.toggle('computer-mode',next==='computer');
  if(typeof updateState==='function')updateState();
  syncComputerConsoleMode();
  if(selector)selector.value=next;computerNode('computer-workspace').hidden=next!=='computer';
  try{localStorage.setItem('deckAppMode',next);}catch{}
  if(next==='computer'){renderComputer();await refreshComputer();}else if(typeof refresh==='function')await refresh(true);
  if(typeof refreshMonitorActivity==='function')refreshMonitorActivity();return true;
}
function computerActivePreset(snapshot=computerSnapshot){return snapshot?.settings?.presets?.find(preset=>preset.id===snapshot.activePresetId);}
function computerMatchesForeground(preset,snapshot=computerSnapshot){
  const normalize=value=>String(value||'').toLowerCase().replace(/\.exe$/,'');
  return snapshot?.foreground?.available===true&&preset.applications.some(app=>normalize(app)===normalize(snapshot.foreground.application));
}
function computerCanRun(control,preset,snapshot=computerSnapshot){return !computerSuspended&&!computerChoosing&&computerReachable&&snapshot?.settings?.enabled===true&&!!snapshot.contextToken&&!!control.shortcut&&(control.global===true||computerMatchesForeground(preset,snapshot));}
async function refreshComputer(){
  if(computerReadPending||computerSuspended||computerMode!=='computer'||document.hidden||panel.open)return;
  const epoch=computerEpoch;computerReadPending=true;
  try{const result=await api('/api/computer');if(epoch!==computerEpoch||computerMode!=='computer')return;computerSnapshot=result;computerReachable=true;renderComputer();}
  catch(error){if(epoch!==computerEpoch)return;computerReachable=false;computerSignature='';renderComputer(error.message);}
  finally{computerReadPending=false;}
}
async function chooseComputerPreset(id){
  if(preview){notice('Connect your tablet to choose its PC control layout.');return;}
  if(!computerNavigationAllowed()||dirty||panel.open)return;
  await writeComputerSelection('/api/computer/preset',{presetId:id},'Layout pinned. Choose Follow app to resume automatic switching.');
}
async function setComputerFollowing(automatic){
  if(preview){notice('Connect your tablet to change how layouts follow your PC.');return;}
  if(typeof automatic!=='boolean'||!computerSnapshot||!computerReachable||!computerNavigationAllowed()||dirty||panel.open||computerMode!=='computer')return;
  if(computerSnapshot.settings.automatic===automatic)return;
  await writeComputerSelection('/api/computer/follow',{automatic},automatic?'Layouts now follow supported PC apps.':'Current layout pinned. Choose Follow app to resume automatic switching.');
}
async function writeComputerSelection(path,body,message){
  // Invalidate an older context read so it cannot restore the pre-selection mode.
  const epoch=++computerEpoch;computerChoosing=true;computerSignature='';renderComputer();let applied=false;
  try{await api(path,body);applied=true;const snapshot=await api('/api/computer');if(epoch===computerEpoch){computerSnapshot=snapshot;computerReachable=true;notice(message);}}
  catch(error){if(epoch===computerEpoch){if(applied)computerReachable=false;notice((applied?'Layout saved, but its status could not refresh. ':'')+error.message,true);}}
  finally{computerChoosing=false;computerSignature='';if(epoch===computerEpoch)renderComputer();}
}
function renderComputerFollowing(host,snapshot){
  const row=computerText('section','computer-follow'),buttons=computerText('div','computer-follow-buttons');buttons.setAttribute('role','group');buttons.setAttribute('aria-label','Layout following');
  for(const [automatic,label,id] of [[true,'Follow app','follow'],[false,'Pin layout','pin']]){const button=computerText('button','quiet',label);button.type='button';button.id='computer-'+id;button.dataset.computerFocus=id;button.setAttribute('aria-pressed',String(snapshot.settings.automatic===automatic));button.disabled=computerChoosing||computerSuspended||!computerReachable;button.onclick=()=>setComputerFollowing(automatic);buttons.append(button);}
  const description=computerText('p','computer-follow-description',computerChoosing?'Updating layout selection…':snapshot.settings.automatic?'Following supported PC apps. Browser layouts stay manual.':'This layout stays selected when you change PC apps.');description.id='computer-follow-status';description.setAttribute('role','status');
  row.append(buttons,description);host.append(row);
}
async function runComputerControl(presetId,controlId,button){
  const snapshot=computerSnapshot,preset=computerActivePreset(snapshot),control=preset?.controls.find(item=>item.id===controlId);
  if(preview){notice('Connect your tablet to the running companion to use Computer Copilot.');return;}
  if(['spotify','youtube'].includes(presetId))return;
  if(!computerNavigationAllowed()||dirty||editing||panel.open||computerMode!=='computer')return;
  if(!preset||preset.id!==presetId||!control||!computerCanRun(control,preset,snapshot)){notice('Open the matching app on the PC, then refresh these controls.',true);return;}
  busy=true;executionPending=control.label;button.disabled=true;button.classList.add('pressed');renderExecution();
  try{const running=api('/api/computer/action',{presetId,controlId,contextToken:snapshot.contextToken});pollExecutionOnce();await running;notice(control.label+' — done');}
  catch(error){notice(error.message,true);}finally{busy=false;executionPending='';button.classList.remove('pressed');computerSignature='';await pollExecutionOnce();renderExecution();await refreshComputer();}
}
function renderComputer(error=''){
  const host=computerNode('computer-workspace');if(!host||computerMode!=='computer')return;
  if(busy)return;
  const snapshot=computerSnapshot,preset=computerActivePreset(),desktop=typeof desktopConsole!=='undefined'&&desktopConsole;
  if(window.MediaTouchBar?.isInteracting(preset?.id))return;
  const signature=JSON.stringify([snapshot?.settings,snapshot?.activePresetId,snapshot?.foreground,snapshot?.status,snapshot?.capabilities,computerReachable,computerSuspended,computerChoosing,desktop,error]);
  if(signature===computerSignature)return;computerSignature=signature;
  const focused=document.activeElement?.dataset?.computerFocus;
  const previousRail=host.querySelector('.computer-apps');if(previousRail)computerRailScroll=previousRail.scrollLeft;
  host.replaceChildren();host.className='computer-workspace';host.setAttribute('aria-label','Computer Copilot');
  const heading=computerText('header','computer-heading'),copy=computerText('div');copy.append(computerText('h2','','Computer Copilot'));if(desktop)copy.append(computerText('p','','Set up contextual controls here. Run them from your tablet.'));heading.append(copy);
  heading.hidden=native&&!desktop;
  const tools=computerText('div','computer-tools'),refreshButton=computerText('button','quiet','Refresh');refreshButton.type='button';refreshButton.dataset.computerFocus='refresh';refreshButton.onclick=refreshComputer;tools.append(refreshButton);
  if(!native){const settings=computerText('button','primary','Edit Copilot settings');settings.type='button';settings.onclick=showComputerSettings;tools.append(settings);}heading.append(tools);host.append(heading);
  const status=computerText('p','computer-status',error?'Connection lost. '+error+' Refresh to reconnect.':!snapshot?'Checking your PC…':!snapshot.settings.enabled?'Computer Copilot is off. Enable it in the Windows app’s Copilot settings.':snapshot.status||'Ready');status.id='computer-status';status.setAttribute('role','status');host.append(status);
  if(!snapshot)return;
  if(desktop){renderComputerManagement(host,snapshot);return;}
  renderComputerFollowing(host,snapshot);
  const rail=computerText('nav','computer-apps');rail.setAttribute('aria-label','App control layouts');
  for(const item of snapshot.settings.presets){const button=computerText('button','computer-app');button.type='button';button.append(computerIcon(item.id),computerText('span','',item.label));button.setAttribute('aria-pressed',String(item.id===preset?.id));button.dataset.computerFocus='preset-'+item.id;button.onclick=()=>chooseComputerPreset(item.id);rail.append(button);}host.append(rail);
  const context=computerText('div','computer-context');context.append(computerIcon(preset?.id),computerText('h3','',preset?.label||'Choose a layout'));const observed=snapshot.foreground?.available?snapshot.foreground.application:'No foreground app detected';context.append(computerText('span','',['spotify','youtube'].includes(preset?.id)?'Controls the selected player':observed+(snapshot.settings.automatic?' · Automatic context':' · Manual layout')));host.append(context);
  if(preset&&['spotify','youtube'].includes(preset.id)){
    if(window.MediaTouchBar)window.MediaTouchBar.attach(host,preset.id,{request:api,notice,isActive:()=>computerMode==='computer'&&!computerSuspended&&!document.hidden&&!panel.open&&computerActivePreset()?.id===preset.id,isEnabled:()=>computerReachable&&computerSnapshot?.settings.enabled===true&&!preview,canWrite:computerNavigationAllowed});
    else host.append(computerText('p','computer-empty','Media controls could not load. Reopen the tablet app.'));
  }else{window.MediaTouchBar?.unmount();if(preset)renderComputerTools(host,preset);}
  appendComputerDisplayStatus(host,snapshot);
  revealComputerPreset(rail,preset?.id);
  if(focused){const target=[...host.querySelectorAll('[data-computer-focus]')].find(node=>node.dataset.computerFocus===focused);target?.focus({preventScroll:true});}
}
function renderComputerTools(host,preset){
  const match=computerMatchesForeground(preset),groups=new Map();if(!match&&preset.controls.some(control=>!control.global))host.append(computerText('p','computer-focus-note','Open '+preset.label+' on the PC to use its app shortcuts.'));
  if(preset.id==='resolve')renderComputerJog(host,preset);
  for(const control of preset.controls){const name=control.group||'Controls';if(!groups.has(name))groups.set(name,[]);groups.get(name).push(control);}
  const bank=computerText('div','computer-bank');for(const [name,controls] of groups){const group=computerText('section','computer-group');group.append(computerText('h4','',name));const keys=computerText('div','computer-keys');for(const control of controls){const button=computerText('button','computer-control');button.type='button';button.disabled=!computerCanRun(control,preset);const glyph=computerToolIcon(control,preset);if(glyph){button.classList.add('has-control-icon');button.append(glyph);}button.append(computerText('span','computer-control-label',control.label),computerText('span','computer-shortcut',control.shortcut||'Assign on PC'));button.dataset.computerFocus='control-'+control.id;button.onclick=()=>runComputerControl(preset.id,control.id,button);keys.append(button);}group.append(keys);bank.append(group);}host.append(bank);
  if(!preset.controls.length)host.append(computerText('p','computer-empty','No controls in this layout. Add bindings in the Windows app’s Copilot settings.'));
}
function renderComputerJog(host,preset){
  const previous=preset.controls.find(control=>control.shortcut.toUpperCase()==='LEFT'&&!control.global),next=preset.controls.find(control=>control.shortcut.toUpperCase()==='RIGHT'&&!control.global);if(!previous||!next)return;
  const section=computerText('section','computer-jog'),label=computerText('label','','Frame jog'),input=document.createElement('input'),output=computerText('output','','Drag to step through frames');input.type='range';input.min='-4';input.max='4';input.step='1';input.value='0';input.setAttribute('aria-label','Frame jog');input.disabled=!computerCanRun(previous,preset)||!computerCanRun(next,preset);label.append(input);section.append(label,output);host.append(section);
  input.oninput=()=>{output.textContent=Number(input.value)<0?'Previous frames':Number(input.value)>0?'Next frames':'Drag to step through frames';if(!computerJog){if(!computerNavigationAllowed()||!Number(input.value)||input.disabled)return;const gesture={input,output,presetId:preset.id,previous,next,contextToken:computerSnapshot.contextToken,active:true};computerJog=gesture;busy=true;executionPending='Jog timeline';renderExecution();runComputerJog(gesture);}};
  input.onpointerdown=event=>{if(typeof event.pointerId==='number')input.setPointerCapture(event.pointerId);};input.onpointerup=input.onpointercancel=input.onlostpointercapture=input.onblur=()=>stopComputerJog();input.onkeyup=event=>{if(['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Home','End'].includes(event.key))stopComputerJog();};
}
async function runComputerJog(gesture){
  try{while(gesture.active&&!computerSuspended&&!document.hidden&&!panel.open&&computerMode==='computer'&&computerActivePreset()?.id===gesture.presetId){const direction=Number(gesture.input.value);if(!direction)break;const control=direction<0?gesture.previous:gesture.next;await api('/api/computer/action',{presetId:gesture.presetId,controlId:control.id,contextToken:gesture.contextToken});if(!gesture.active)break;await new Promise(resolve=>setTimeout(resolve,Math.max(100,340-Math.abs(direction)*60)));}}
  catch(error){if(!computerSuspended)notice(error.message,true);}finally{if(computerJog===gesture)computerJog=null;gesture.input.value='0';gesture.output.textContent='Drag to step through frames';busy=false;executionPending='';renderExecution();await pollExecutionOnce();await refreshComputer();}
}
function stopComputerJog(){if(computerJog){computerJog.active=false;computerJog.input.value='0';computerJog.output.textContent='Drag to step through frames';}}
function appendComputerDisplayStatus(host,snapshot){
  if(!computerDisplayPanel){
    computerDisplayPanel=computerText('details','computer-display');computerDisplayPanel.id='computer-display';computerDisplayPanel.append(computerText('summary','','Second display'));
    const content=computerText('div');content.id='computer-display-content';computerDisplayPanel.append(content);computerDisplayPanel.addEventListener('toggle',()=>{if(computerDisplayPanel.open)refreshComputerDisplay();else stopComputerDisplayView();});
  }
  host.append(computerDisplayPanel);if(!computerDisplaySnapshot)renderComputerDisplay(snapshot.capabilities?.displayNote);
}
function computerHasNativeDisplay(){return typeof native!=='undefined'&&native===true&&typeof nativeRequest==='function';}
function normalizeComputerDisplayCapabilities(value){
  if(!value||value.available!==true)return{available:false,detail:typeof value?.detail==='string'?value.detail.slice(0,500):'High-refresh display requires the current DeskWing tablet app.'};
  const rate=value.currentRefreshRate,maximum=value.maxRefreshRate;
  if(!Number.isFinite(rate)||rate<1||rate>240||!Number.isFinite(maximum)||maximum<rate-1||maximum>240)throw new Error('The tablet did not report a supported refresh rate.');
  return{available:true,currentRefreshRate:rate,maxRefreshRate:maximum,supportedRefreshRates:Array.isArray(value.supportedRefreshRates)?value.supportedRefreshRates.filter(rate=>Number.isFinite(rate)&&rate>=1&&rate<=240):[],detail:typeof value.detail==='string'?value.detail.slice(0,500):''};
}
function computerDisplayRateProblem(){
  const source=computerDisplaySnapshot?.refreshRate,target=computerDisplayRefreshMode==='match'&&computerDisplayCapabilities?.available?Math.round(computerDisplayCapabilities.currentRefreshRate):60;
  if(computerDisplayRefreshMode==='match'&&target<60)return 'Match tablet is unavailable while the tablet reports below 60 Hz. Check its power and display settings, or choose 60 Hz.';
  return Number.isFinite(source)&&source>0&&source+0.1<target?`Create the PC Second display at ${target} Hz or higher to use this setting.`:'';
}
function updateComputerDisplayRateUI(){
  const problem=computerDisplayRateProblem(),warning=computerNode('computer-display-rate-warning'),start=computerNode('computer-display-start');
  if(warning){warning.textContent=problem;warning.hidden=!problem;}
  if(start){start.disabled=computerDisplayNativeStarting||!!problem;start.setAttribute('aria-describedby',problem?'computer-display-rate-warning':'computer-display-rate-help');}
}
function appendComputerDisplayRefresh(content){
  const capabilities=computerDisplayCapabilities;
  const label=computerText('label','computer-display-choice','Refresh rate'),select=computerText('select','app-mode');select.id='computer-display-rate';
  const fixed=computerText('option','','60 Hz');fixed.value='60';select.append(fixed);
  const target=capabilities?.available?Math.round(capabilities.currentRefreshRate):null,matchAvailable=target>=60;
  const match=computerText('option','',target?`Match tablet (${target} Hz)`:'Match tablet');match.value='match';match.disabled=!matchAvailable;select.append(match);
  select.value=computerDisplayRefreshMode;select.onchange=()=>{computerDisplayRefreshMode=select.value==='match'?'match':'60';updateComputerDisplayRateUI();};label.append(select);content.append(label);
  const rates=capabilities?.available?`Tablet: ${Math.round(capabilities.currentRefreshRate)} Hz now · up to ${Math.round(capabilities.maxRefreshRate)} Hz. `:'';
  const unavailable=target&&!matchAvailable?'Match tablet is unavailable below 60 Hz. Check the tablet’s power and display settings, or choose 60 Hz. ':'';
  const help=computerText('p','computer-display-rate-help',rates+unavailable+'The viewer shows the received video frame rate. The extra Windows display must be set to at least the selected rate.');help.id='computer-display-rate-help';content.append(help);
  const warning=computerText('p','computer-display-rate-warning');warning.id='computer-display-rate-warning';warning.setAttribute('role','status');warning.hidden=true;content.append(warning);
  const qualityLabel=computerText('label','computer-display-choice','Picture and power'),quality=computerText('select','app-mode');quality.id='computer-display-quality';
  for(const [value,label] of [['balanced','Balanced'],['quality','Sharper picture'],['battery-saver','Save power and bandwidth']]){const option=computerText('option','',label);option.value=value;quality.append(option);}
  quality.value=computerDisplayQualityPreset;quality.onchange=()=>{computerDisplayQualityPreset=['balanced','quality','battery-saver'].includes(quality.value)?quality.value:'balanced';};qualityLabel.append(quality);content.append(qualityLabel);
  const adaptiveLabel=computerText('label','computer-display-consent'),adaptive=document.createElement('input');adaptive.type='checkbox';adaptive.id='computer-display-adaptive';adaptive.checked=computerDisplayAdaptiveQuality;adaptive.onchange=()=>{computerDisplayAdaptiveQuality=adaptive.checked;};adaptiveLabel.append(adaptive,computerText('span','','Adjust quality when the connection is busy'));content.append(adaptiveLabel);
  content.append(computerText('p','computer-display-rate-help','These choices keep your selected resolution and refresh target. In the viewer, choose Pen or Touch only after allowing tablet input on the PC.'));
}
function appendComputerDisplayInputPermission(content,data){
  const state=computerDisplayInputStatus;if(!state)return;
  const section=computerText('section','computer-display-input'),title=computerText('h4','','Pen and touch on this display');section.append(title);
  const label=computerText('label','computer-display-consent'),allow=document.createElement('input');allow.type='checkbox';allow.id='computer-display-input-enabled';allow.checked=state.enabled===true;allow.disabled=state.supported!==true||!data.sharedWithTablet||typeof state.targetIdentity!=='string'||!state.targetIdentity;
  label.append(allow,computerText('span','','Allow my paired tablet to tap and draw on this extra display'));section.append(label);
  const details=computerText('p','computer-display-rate-help',state.error||'Viewing starts without input. Choose Pen for your stylus or Touch for single-finger taps and drags in the tablet viewer.');details.id='computer-display-input-status';details.setAttribute('role','status');section.append(details);content.append(section);
  allow.onchange=async()=>{
    const enabled=allow.checked,epoch=computerEpoch,targetIdentity=state.targetIdentity;
    if(!computerNavigationAllowed()){allow.checked=state.enabled===true;return;}
    allow.disabled=true;
    try{
      if(enabled&&!await confirmAction('Allow tablet pen and touch?','Your paired tablet can tap and draw on this extra Windows display while viewing it. Input stops when that viewing session ends.')){allow.checked=state.enabled===true;return;}
      if(epoch!==computerEpoch)return;
      if(computerDisplaySnapshot?.sourceName!==data.sourceName||computerDisplayInputStatus?.targetIdentity!==targetIdentity){allow.checked=computerDisplayInputStatus?.enabled===true;details.textContent='The extra display changed. Review its current input permission.';await refreshComputerDisplay();return;}
      await api('/api/admin/computer/display/input',{enabled,targetIdentity});
      if(epoch===computerEpoch)await refreshComputerDisplay();
    }catch(error){if(epoch===computerEpoch){allow.checked=state.enabled===true;details.textContent=error.message;}}
    finally{if(allow.isConnected)allow.disabled=state.supported!==true||!data.sharedWithTablet;}
  };
}
function renderComputerDisplay(fallback=''){
  const content=computerDisplayPanel?.querySelector('#computer-display-content');if(!content)return;
  // Keep an active image mounted while foreground context changes in the control bar.
  if(computerDisplayWanted)return;
  const desktop=typeof desktopConsole!=='undefined'&&desktopConsole,signature=JSON.stringify([computerDisplaySnapshot,computerDisplayCapabilities,computerDisplayInputStatus,computerDisplayVideoStatus,computerDisplayNativeStarting,fallback,desktop]);if(signature===computerDisplaySignature)return;computerDisplaySignature=signature;
  content.replaceChildren();const data=computerDisplaySnapshot;
  const note=computerText('p','',data?.detail||fallback||'Check whether an independent Windows display is available.');note.id='computer-display-status';note.setAttribute('role','status');content.append(note);
  const row=computerText('div','computer-display-tools'),check=computerText('button','quiet','Check display');check.type='button';check.onclick=refreshComputerDisplay;row.append(check);
  if(data?.active&&data.independentDesktop){
    const rate=Number.isFinite(data.refreshRate)&&data.refreshRate>0?` · Windows display ${Number(data.refreshRate.toFixed(2))} Hz`:'';
    content.append(computerText('p','',`Independent desktop · ${data.width} × ${data.height}${rate} · ${data.sharedWithTablet?'Sharing with your paired tablet is enabled.':'Tablet sharing is off.'}`));
    if(!desktop&&data.sharedWithTablet){
      if(computerHasNativeDisplay()&&computerDisplayCapabilities?.available){appendComputerDisplayRefresh(content);const view=computerText('button','primary',computerDisplayNativeStarting?'Starting display…':'Start second display');view.id='computer-display-start';view.type='button';view.disabled=computerDisplayNativeStarting;view.onclick=startComputerDisplayView;row.append(view);}
      else content.append(computerText('p','computer-display-rate-help',computerDisplayCapabilities?.detail||(computerHasNativeDisplay()?'Checking tablet display support…':'Use the DeskWing tablet app for 60 Hz or Match tablet.')));
      const compatibility=computerText('button','quiet','Open compatibility preview');compatibility.type='button';compatibility.disabled=computerDisplayNativeStarting;compatibility.onclick=startComputerDisplayPreview;row.append(compatibility);
      content.append(computerText('p','computer-display-preview-help','Compatibility preview sends still images at up to 4 fps. It has no audio.'));
      if(computerDisplayInputStatus)content.append(computerText('p','computer-display-rate-help',computerDisplayInputStatus.enabled?'Pen and single-finger Touch are available in the native viewer. Each session starts in View mode.':'To tap or draw on this desktop, allow tablet pen and touch in the PC Second display settings.'));
    }
    if(desktop)appendComputerDisplayInputPermission(content,data);
    if(desktop&&computerDisplayVideoStatus?.active){const video=computerDisplayVideoStatus,preset={balanced:'Balanced',quality:'Sharper picture','battery-saver':'Save power and bandwidth'}[video.qualityPreset]||'Current profile',encoder=typeof video.encoder==='string'?video.encoder.slice(0,120):'Windows H.264';content.append(computerText('p','computer-display-rate-help',`${preset} · ${video.hardwareAccelerated?'Hardware':'Software'} encoding · ${encoder}${Number.isFinite(video.bitrate)?' · '+(video.bitrate/1000000).toFixed(1)+' Mbps':''}${video.adaptiveQuality?' · Adaptive quality on':''}`));}
    if(desktop){const share=computerText('button','quiet',data.sharedWithTablet?'Pause tablet sharing':'Allow tablet sharing');share.type='button';share.onclick=async()=>{if(!data.sharedWithTablet&&!await confirmAction('Share the independent display?','Everything shown on this extra Windows display becomes visible to your paired tablet.'))return;await changeComputerDisplay({operation:'share',enabled:!data.sharedWithTablet},share);};row.append(share);const remove=computerText('button','danger','Remove extra display');remove.type='button';remove.onclick=async()=>{if(await confirmAction('Remove the extra display?','Windows will move its windows to your remaining displays.'))await changeComputerDisplay({operation:'remove'},remove);};row.append(remove);}
  }else if(desktop&&data?.available){
    content.append(computerText('p','','Create a separate Windows desktop using the installed display driver. Windows can place apps on it independently of your physical monitors. Its pixels will be visible to your paired tablet.'));
    if(data.driver){content.append(computerText('p','',`${data.driver.name||'Installed driver'}${data.driver.version?' · '+data.driver.version:''}${data.driver.signer?' · Signed by '+data.driver.signer:''}`));if(data.driver.detail)content.append(computerText('p','',data.driver.detail));}
    const sizeLabel=computerText('label','computer-display-choice','Resolution'),size=computerText('select','app-mode');size.id='computer-display-size';for(const [value,label] of [['1280x800','1280 × 800'],['1600x900','1600 × 900'],['1920x1080','1920 × 1080']]){const option=computerText('option','',label);option.value=value;size.append(option);}sizeLabel.append(size);content.append(sizeLabel);
    const rateLabel=computerText('label','computer-display-choice','Windows display refresh rate'),rate=computerText('select','app-mode');rate.id='computer-display-refresh-rate';for(const value of [60,90,120,144,165,240]){const option=computerText('option','',`${value} Hz`);option.value=String(value);rate.append(option);}rateLabel.append(rate);content.append(rateLabel);
    content.append(computerText('p','computer-display-rate-help','Choose at least your tablet’s active refresh rate to use Match tablet. This sets the Windows display; the tablet viewer reports the received video frame rate.'));
    const accept=computerText('label','computer-display-consent'),driver=document.createElement('input');driver.type='checkbox';accept.append(driver,computerText('span','','Use the installed '+(data.driver?.name||'virtual display')+' driver to create an extra Windows display.'));content.append(accept);
    const consent=computerText('label','computer-display-consent'),share=document.createElement('input');share.type='checkbox';consent.append(share,computerText('span','','Allow my paired tablet to view everything on this extra display.'));content.append(consent);
    const create=computerText('button','primary','Create independent display');create.type='button';create.disabled=true;driver.onchange=share.onchange=()=>{create.disabled=!driver.checked||!share.checked;};create.onclick=()=>{if(!driver.checked||!share.checked)return;const [width,height]=size.value.split('x').map(Number);return changeComputerDisplay({operation:'create',width,height,refreshRate:Number(rate.value),shareWithTablet:true,acceptInstalledDriver:true},create);};row.append(create);
  }else if(data&&!data.active){content.append(computerText('p','',desktop?'An installed, supported virtual display driver is required. Contextual controls work without an extra display.':'Create and enable sharing for an independent display in the Windows app. Contextual controls work without it.'));}
  content.append(row);updateComputerDisplayRateUI();
}
async function refreshComputerDisplay(){
  if(computerDisplayReading||computerSuspended||computerMode!=='computer'||document.hidden||panel.open)return;computerDisplayReading=true;const epoch=computerEpoch;
  try{
    const [status,capabilities,input,video]=await Promise.allSettled([api('/api/computer/display'),computerHasNativeDisplay()?nativeRequest('/local/display/capabilities',{},'POST',5000):Promise.resolve(null),api('/api/computer/display/input'),api('/api/computer/display/stream/status')]);
    if(epoch!==computerEpoch)return;if(status.status==='rejected')throw status.reason;
    const data=status.value;computerDisplaySnapshot=data;computerDisplayInputStatus=input.status==='fulfilled'&&input.value&&typeof input.value.supported==='boolean'&&typeof input.value.enabled==='boolean'?input.value:null;computerDisplayVideoStatus=video.status==='fulfilled'&&video.value&&typeof video.value.active==='boolean'?video.value:null;
    try{computerDisplayCapabilities=normalizeComputerDisplayCapabilities(capabilities.status==='fulfilled'?capabilities.value:null);}catch(error){computerDisplayCapabilities={available:false,detail:error.message};}
    if(computerDisplayWanted&&(!data.active||!data.independentDesktop||!data.sharedWithTablet)){stopComputerDisplayView();notice('The independent display is no longer shared. Enable it again in the Windows app.',true);}renderComputerDisplay();
  }
  catch(error){if(epoch!==computerEpoch)return;stopComputerDisplayView();computerDisplaySnapshot=null;renderComputerDisplay('Display status unavailable. '+error.message+' Check display to try again.');}
  finally{computerDisplayReading=false;}
}
async function changeComputerDisplay(body,button){
  if(native||!computerNavigationAllowed()||dirty||panel.open)return;button.disabled=true;
  try{await api('/api/admin/computer/display',body,30000);await refreshComputerDisplay();}
  catch(error){notice(error.message,true);}finally{if(button.isConnected)button.disabled=false;}
}
function stopComputerDisplayView(notifyNative=true){
  const session=computerDisplayNativeSession;computerDisplayNativeSession=null;computerDisplayTransport='';
  computerDisplayWanted=false;computerDisplayEpoch++;computerDisplaySignature='';clearTimeout(computerDisplayTimer);document.body.classList.remove('computer-screen-mode');
  const image=computerNode('computer-display-image');if(image)image.removeAttribute('src');if(computerDisplayPanel)renderComputerDisplay();
  if(notifyNative&&session&&computerHasNativeDisplay())void nativeRequest('/local/display/stop',{session},'POST',5000).catch(()=>{});
}
function computerDisplayCanStart(){const data=computerDisplaySnapshot;return!!(data?.active&&data.independentDesktop&&data.sharedWithTablet&&computerMode==='computer'&&!computerDisplayWanted&&!computerDisplayNativeStarting&&computerNavigationAllowed());}
async function startComputerDisplayView(){
  if(!computerDisplayCanStart()||!computerHasNativeDisplay()||!computerDisplayCapabilities?.available||computerDisplayRateProblem())return;
  const session=crypto.randomUUID(),epoch=++computerDisplayEpoch;
  computerDisplayWanted=true;computerDisplayNativeStarting=true;computerDisplayNativeSession=session;computerDisplayTransport='native';
  const content=computerNode('computer-display-content');content.replaceChildren();const status=computerText('p','','Opening the tablet display…');status.id='computer-display-status';status.setAttribute('role','status');content.append(status);
  const tools=computerText('div','computer-display-tools'),stop=computerText('button','quiet','Back to controls');stop.type='button';stop.onclick=()=>stopComputerDisplayView();tools.append(stop);content.append(tools);
  try{
    const result=await nativeRequest('/local/display/start',{refreshMode:computerDisplayRefreshMode,qualityPreset:computerDisplayQualityPreset,adaptiveQuality:computerDisplayAdaptiveQuality,session},'POST',20000);
    if(epoch!==computerDisplayEpoch||!computerDisplayWanted||computerDisplayNativeSession!==session){void nativeRequest('/local/display/stop',{session},'POST',5000).catch(()=>{});return;}
    if(result?.started!==true||result.session!==session||!Number.isFinite(result.targetRefreshRate)||result.targetRefreshRate<60||result.targetRefreshRate>240)throw new Error('The tablet did not start a supported display session.');
    status.textContent=`Second display is open on the tablet · ${Math.round(result.targetRefreshRate)} Hz target. The viewer reports its received video frame rate.`;
  }catch(error){if(epoch===computerDisplayEpoch&&computerDisplayNativeSession===session){stopComputerDisplayView();notice('Second display could not start. '+(error?.message||'Check the PC connection and try again.'),true);}}
  finally{computerDisplayNativeStarting=false;computerDisplaySignature='';if(!computerDisplayWanted)renderComputerDisplay();}
}
function startComputerDisplayPreview(){
  if(!computerDisplayCanStart())return;
  const data=computerDisplaySnapshot;if(!data?.active||!data.independentDesktop||!data.sharedWithTablet||computerMode!=='computer'||!computerNavigationAllowed())return;
  computerDisplayWanted=true;computerDisplayTransport='preview';const epoch=++computerDisplayEpoch,content=computerNode('computer-display-content');content.replaceChildren();
  const tools=computerText('div','computer-display-tools'),back=computerText('button','quiet','Back to controls');back.type='button';back.onclick=stopComputerDisplayView;tools.append(back);content.append(tools);
  const image=document.createElement('img');image.id='computer-display-image';image.alt='Independent Windows display';content.append(image);const status=computerText('p','',`Opening ${data.sourceName||'the independent display'}…`);status.id='computer-display-status';status.setAttribute('role','status');content.append(status);document.body.classList.add('computer-screen-mode');computerDisplayTick(epoch);
}
async function computerDisplayTick(epoch){
  if(!computerDisplayWanted||computerSuspended||epoch!==computerDisplayEpoch||computerMode!=='computer'||document.hidden||panel.open)return;
  if(computerDisplayPending){computerDisplayTimer=setTimeout(()=>computerDisplayTick(epoch),100);return;}computerDisplayPending=true;
  try{const frame=await api('/api/computer/display/frame',{width:960},10000);if(epoch!==computerDisplayEpoch||!computerDisplayWanted)return;if(typeof frame.image!=='string'||!frame.image.startsWith('data:image/jpeg;base64,')||frame.image.length>4000000)throw new Error('The display returned an invalid image.');computerNode('computer-display-image').src=frame.image;computerNode('computer-display-status').textContent=`Compatibility preview · up to 4 fps · ${frame.width} × ${frame.height} · no audio`;}
  catch(error){if(epoch!==computerDisplayEpoch)return;stopComputerDisplayView();notice('Display view paused. '+error.message,true);}
  finally{computerDisplayPending=false;if(computerDisplayWanted&&epoch===computerDisplayEpoch)computerDisplayTimer=setTimeout(()=>computerDisplayTick(epoch),250);}
}
function renderComputerManagement(host,snapshot){
  const facts=computerText('dl','computer-facts');for(const [label,value] of [['Controls',snapshot.settings.enabled?'Enabled':'Off until enabled'],['Layout selection',snapshot.settings.automatic?'Follows supported foreground apps':'Manual app layout'],['Foreground app',snapshot.foreground?.available?snapshot.foreground.application:'Unavailable'],['Current layout',computerActivePreset(snapshot)?.label||'None'],['Saved layouts',String(snapshot.settings.presets.length)]]){const row=computerText('div');row.append(computerText('dt','',label),computerText('dd','',value));facts.append(row);}host.append(facts);
  host.append(computerText('p','computer-management-help','Edit labels, shortcuts, app associations and your own tool layouts. Selecting a layout changes the tablet controls; it does not open or focus a PC app. Browser tools use a manual layout because website titles are not inspected.'));
  appendComputerDisplayStatus(host,snapshot);
}
async function showComputerSettings(){
  if(native||preview)return;if(!computerNavigationAllowed()||dirty){notice('Save or discard your changes before opening Copilot settings.',true);return;}
  stopComputerDisplayView();
  featureView='computer-settings';editing=true;openPanel('Computer Copilot settings','<p role="status">Loading saved layouts…</p><p id="computer-settings-error" role="alert"></p>');
  let generation=panelGeneration;const owns=()=>panel.open&&featureView==='computer-settings'&&generation===panelGeneration;
  try{const settings=await api('/api/admin/computer');if(!owns())return;renderComputerSettings(settings,0);}
  catch(error){if(owns())computerNode('computer-settings-error').textContent=error.message;}
}
function renderComputerSettings(settings,selected){
  const presets=settings.presets,preset=presets[selected];let saving=false;
  const controls=preset?.controls||[];
  openPanel('Computer Copilot settings',`<p class="help">The tablet becomes a contextual shortcut bar for your PC. Start with the supplied layouts, then match shortcuts to your apps. Controls stay off until you enable them.</p><div class="row"><label class="check"><input id="computer-enabled" type="checkbox" ${settings.enabled?'checked':''}>Enable Computer Copilot</label><label class="check"><input id="computer-automatic" type="checkbox" ${settings.automatic?'checked':''}>Follow supported foreground apps</label></div><div class="form-grid"><label>Manual / fallback layout<select id="computer-manual">${presets.map(item=>`<option value="${esc(item.id)}" ${item.id===settings.manualPresetId?'selected':''}>${esc(item.label)}</option>`).join('')}</select></label></div><p class="help">Browser websites are not detected. Choose their layout on the tablet. Choosing a layout never launches or focuses an app.</p><div class="editor-layout"><section><label>Edit layout<select id="computer-preset">${presets.map((item,index)=>`<option value="${index}" ${index===selected?'selected':''}>${esc(item.label)}</option>`).join('')}</select></label><div class="row"><button id="computer-add-preset" class="quiet">Add tool layout</button></div><p class="help">Create a layout for any tool with Windows keyboard shortcuts.</p></section><section>${preset?`<div class="form-grid"><label>Layout name<input id="computer-preset-label" maxlength="60" value="${esc(preset.label)}"></label><label>Windows apps (.exe names, separated by commas)<input id="computer-applications" value="${esc(preset.applications.join(', '))}" placeholder="Code.exe, Codex.exe"></label></div><p class="help">These app names protect foreground shortcuts from reaching the wrong app. Media keys and Alt+Tab can be global. Confirm your app’s shortcut settings before use.</p><div id="computer-binding-list" class="computer-binding-list">${controls.map((control,index)=>`<fieldset class="computer-binding"><legend>Control ${index+1}</legend><div class="form-grid"><label>Label<input data-computer-label="${index}" maxlength="60" value="${esc(control.label)}"></label><label>Group<input data-computer-group="${index}" maxlength="40" value="${esc(control.group)}"></label><label>Shortcut<input data-computer-shortcut="${index}" maxlength="100" value="${esc(control.shortcut)}" placeholder="CTRL+SHIFT+P" autocapitalize="characters" spellcheck="false"></label><label class="computer-global"><input type="checkbox" data-computer-global="${index}" ${control.global?'checked':''}>Global media / app switch</label></div><button type="button" class="quiet" data-computer-remove="${index}">Remove control</button></fieldset>`).join('')}</div><div class="row"><button id="computer-add-control" class="quiet">Add control</button><button id="computer-remove-preset" class="danger" ${presets.length<2?'disabled':''}>Remove layout</button></div>`:'<p>Add a tool layout to configure its controls.</p>'}</section></div><p id="computer-settings-error" class="help" role="alert"></p><div class="save-bar"><span id="computer-save-status" class="help">${dirty?'Unsaved changes':'Saved bindings sync to your tablet.'}</span><button id="computer-save" class="primary">Save Copilot settings</button></div>`);
  featureView='computer-settings';editing=true;const generation=panelGeneration,owns=()=>panel.open&&panelGeneration===generation&&featureView==='computer-settings';
  const mark=()=>{dirty=true;computerNode('computer-save-status').textContent='Unsaved changes';};
  const redraw=index=>{if(!saving&&owns())renderComputerSettings(settings,index);};
  for(const [id,key] of [['computer-enabled','enabled'],['computer-automatic','automatic']])computerNode(id).onchange=event=>{settings[key]=event.target.checked;mark();};
  computerNode('computer-manual').onchange=event=>{settings.manualPresetId=event.target.value;mark();};computerNode('computer-preset').onchange=event=>redraw(Number(event.target.value));
  const unique=prefix=>prefix+'-'+crypto.randomUUID().replaceAll('-','').slice(0,12);
  computerNode('computer-add-preset').onclick=()=>{if(saving||presets.length>=16){notice('Keep up to 16 layouts.',true);return;}presets.push({id:unique('tool'),label:'My tool',applications:[],controls:[{id:unique('control'),label:'New control',group:'Controls',shortcut:'',global:false}]});mark();redraw(presets.length-1);};
  if(preset){computerNode('computer-preset-label').oninput=event=>{preset.label=event.target.value;mark();};computerNode('computer-applications').oninput=event=>{preset.applications=event.target.value.split(',').map(value=>value.trim()).filter(Boolean);mark();};
    for(const key of ['label','group','shortcut','global'])for(const field of computerNode('computer-binding-list').querySelectorAll('[data-computer-'+key+']'))field.oninput=event=>{const index=Number(field.dataset['computer'+key[0].toUpperCase()+key.slice(1)]);controls[index][key]=key==='global'?event.target.checked:event.target.value;mark();};
    for(const button of computerNode('computer-binding-list').querySelectorAll('[data-computer-remove]'))button.onclick=()=>{if(saving)return;if(controls.length<2){notice('Keep at least one control. Leave its shortcut blank to disable it.',true);return;}controls.splice(Number(button.dataset.computerRemove),1);mark();redraw(selected);};
    computerNode('computer-add-control').onclick=()=>{if(saving||controls.length>=40){notice('Keep up to 40 controls in each layout.',true);return;}controls.push({id:unique('control'),label:'New control',group:'Controls',shortcut:'',global:false});mark();redraw(selected);};
    computerNode('computer-remove-preset').onclick=async()=>{if(saving||presets.length<2||!await confirmAction('Remove '+preset.label+'?','Its controls will be removed when you save Copilot settings.')||!owns()||saving)return;presets.splice(selected,1);if(settings.manualPresetId===preset.id)settings.manualPresetId=presets[0].id;mark();redraw(Math.min(selected,presets.length-1));};
  }
  computerNode('computer-save').onclick=async()=>{
    if(saving||!owns()||!computerNavigationAllowed())return;saving=true;const body=JSON.parse(JSON.stringify(settings));for(const node of computerNode('panel-body').querySelectorAll('input,select,button'))node.disabled=true;computerNode('computer-settings-error').textContent='';
    try{await api('/api/admin/computer',body);if(!owns())return;dirty=false;computerSignature='';computerNode('computer-save-status').textContent='Saved. Your tablet will update automatically.';}
    catch(error){if(owns())computerNode('computer-settings-error').textContent=error.message;}
    finally{saving=false;if(owns()){for(const node of computerNode('panel-body').querySelectorAll('input,select,button'))node.disabled=false;if(computerNode('computer-remove-preset'))computerNode('computer-remove-preset').disabled=presets.length<2;}}
  };
  if(preset&&['spotify','youtube'].includes(preset.id)){
    computerNode('computer-binding-list').hidden=true;computerNode('computer-add-control').hidden=true;computerNode('computer-remove-preset').before(computerText('p','help','Playback, progress and app volume use the selected Windows media player. Legacy playback and system-volume shortcuts are unavailable in this layout.'));
  }
}
function initializeComputerCopilot(){
  if(!computerNode('computer-workspace'))return;
  const suspendMonitor=window.monitorSuspend,resumeMonitor=window.monitorResume;
  window.monitorSuspend=(...args)=>{computerSuspend();return suspendMonitor?.apply(window,args);};
  window.monitorResume=(...args)=>{const result=resumeMonitor?.apply(window,args);computerResume();return result;};
  const selector=computerNode('app-mode');if(selector)selector.onchange=()=>setComputerMode(selector.value);
  let saved='studio';try{saved=localStorage.getItem('deckAppMode')||'studio';}catch{}if(saved==='computer')setComputerMode(saved);
  setInterval(()=>{refreshComputer();if(computerDisplayPanel?.open)refreshComputerDisplay();},1500);
  panel.addEventListener('close',()=>{if(!panel.open&&computerMode==='computer'){syncComputerConsoleMode();computerSignature='';refreshComputer();}});
  document.addEventListener('visibilitychange',()=>{if(document.hidden)computerSuspend();else computerResume();});
  document.addEventListener('deskwing-display',event=>{
    const detail=event.detail;if(detail?.active!==false||typeof detail.session!=='string'||detail.session!==computerDisplayNativeSession)return;
    stopComputerDisplayView(false);
    if(typeof detail.reason==='string'&&detail.reason&&detail.reason!=='closed')notice(detail.reason.slice(0,500),true);
  });
  window.addEventListener('pagehide',computerSuspend);
  window.addEventListener('blur',()=>{stopComputerJog();window.MediaTouchBar?.suspend();});
  window.addEventListener('resize',()=>{const rail=computerNode('computer-workspace').querySelector('.computer-apps');if(computerMode==='computer'&&rail){computerRailScroll=rail.scrollLeft;revealComputerPreset(rail,computerActivePreset()?.id,true);}});
}
function computerSuspend(){computerSuspended=true;computerEpoch++;computerSignature='';stopComputerJog();window.MediaTouchBar?.suspend();stopComputerDisplayView();renderComputer();}
function computerResume(){computerSuspended=false;computerSignature='';renderComputer();refreshComputer();window.MediaTouchBar?.refresh();}
document.addEventListener('DOMContentLoaded',initializeComputerCopilot,{once:true});
