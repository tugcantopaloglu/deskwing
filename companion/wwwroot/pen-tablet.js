'use strict';
// Windows Ink is opt-in. An expired, interrupted, or disconnected lease never resumes itself.
(function () {
  const MAX_BATCH = 32, MAX_PENDING = 96, MAX_AGE = 500, TICK = 24, RENEW = 350, REQUEST_TIMEOUT = 850;
  const finite = value => typeof value === 'number' && Number.isFinite(value);
  const clamp = (value, low, high) => Math.max(low, Math.min(high, value));
  const phases = new Set(['hover','down','move','up','leave']);
  function normalizeSample(value) {
    if (!value || !phases.has(value.phase) || ![value.x,value.y,value.pressure,value.tiltX,value.tiltY].every(finite)) throw new Error('The pen sample is invalid.');
    return {phase:value.phase,x:clamp(value.x,0,1),y:clamp(value.y,0,1),pressure:['up','hover','leave'].includes(value.phase) ? 0 : clamp(value.pressure,0,1),tiltX:clamp(value.tiltX,-90,90),tiltY:clamp(value.tiltY,-90,90),barrel:value.barrel === true,eraser:value.eraser === true};
  }
  function makeSample(event, rect, phase) {
    if (!event || event.pointerType !== 'pen' || !finite(event.clientX) || !finite(event.clientY) || !rect || rect.width <= 0 || rect.height <= 0) return null;
    return normalizeSample({phase,x:(event.clientX-rect.left)/rect.width,y:(event.clientY-rect.top)/rect.height,pressure:finite(event.pressure) ? event.pressure : 0.5,tiltX:finite(event.tiltX) ? event.tiltX : 0,tiltY:finite(event.tiltY) ? event.tiltY : 0,barrel:(event.buttons & 2) !== 0,eraser:event.button === 5 || (event.buttons & 32) !== 0});
  }
  function isContact(event) { return event.pointerType === 'pen' && ((event.buttons & 33) !== 0 || event.pressure > 0); }
  function createClient(options) {
    const request = options.request, now = options.now || (() => performance.now()), schedule = options.schedule || setTimeout, unschedule = options.unschedule || clearTimeout, changed = options.onChange || (() => {});
    let state = 'idle', message = 'Start a pen session when your Windows app is ready.', generation = 0, lease = null, sequence = 0, startedAt = 0, sentAt = 0, timer = null, inFlight = false, queue = [], batchLimit = MAX_BATCH;
    const snapshot = () => ({state,message,pending:queue.length,inFlight,sequence});
    const emit = () => changed(snapshot());
    function deadline(promise) {
      return new Promise((resolve,reject) => {
        const timeout = schedule(() => reject(new Error('The PC did not respond in time. Start a new pen session.')),REQUEST_TIMEOUT);
        promise.then(value => {unschedule(timeout);resolve(value);},error => {unschedule(timeout);reject(error);});
      });
    }
    function release(capturedLease, operation = 'cancel') {
      if (!capturedLease) return Promise.resolve();
      return deadline(Promise.resolve().then(() => request({operation,lease:capturedLease},REQUEST_TIMEOUT))).catch(() => {});
    }
    function arm() {
      if (state !== 'active' || timer !== null) return;
      timer = schedule(() => {timer=null;void tick();},TICK);
    }
    function stop(reason = 'Pen session stopped. Start again when you are ready.', error = false, operation = 'cancel') {
      const capturedLease = lease;
      generation++; lease=null; queue=[]; inFlight=false;
      if (timer !== null) unschedule(timer); timer=null;
      state=error ? 'error' : 'idle'; message=reason; emit();
      return release(capturedLease,operation);
    }
    async function start(session) {
      if (state === 'active' || state === 'starting') return false;
      if (typeof session !== 'string' || !/^[a-zA-Z0-9_-]{16,80}$/.test(session)) throw new Error('A new session identifier is required.');
      const token = ++generation;
      state='starting'; message='Starting a Windows pen session…'; queue=[]; emit();
      const raw = Promise.resolve().then(() => request({operation:'begin',session},0));
      // A timeout cannot cancel a request already on the wire. Release its lease if it arrives late.
      raw.then(result => {if (token !== generation && typeof result?.lease === 'string') void release(result.lease);},() => {});
      try {
        const result = await deadline(raw);
        if (token !== generation) return false;
        if (!result || typeof result.lease !== 'string' || !result.lease || result.lease.length > 512 || result.sequence !== 0) {
          if (typeof result?.lease === 'string') void release(result.lease);
          throw new Error('The PC returned an invalid pen session.');
        }
        lease=result.lease; sequence=0; batchLimit=Math.max(1,Math.min(MAX_BATCH,Number.isInteger(result.maxBatch) ? result.maxBatch : MAX_BATCH));
        startedAt=now(); sentAt=startedAt; inFlight=false; state='active'; message='Pen connected. Draw in the Windows app on your PC.'; emit(); arm(); return true;
      } catch (error) {
        if (token === generation) void stop(error?.message || 'Could not start the pen session. Check your PC connection.',true);
        return false;
      }
    }
    function enqueue(value) {
      if (state !== 'active') return false;
      let sample;
      try {sample=normalizeSample(value);} catch {void stop('The pen reported invalid input. Start a new pen session.',true);return false;}
      queue.push({sample,time:now()});
      while (queue.length > MAX_PENDING) {
        const disposable = queue.findIndex(item => item.sample.phase === 'move' || item.sample.phase === 'hover');
        if (disposable < 0) {void stop('Too much pen input is waiting for the PC. Start a new pen session.',true);return false;}
        queue.splice(disposable,1);
      }
      arm(); return true;
    }
    async function tick() {
      if (state !== 'active') return;
      if (queue.length && now()-queue[0].time > MAX_AGE) {void stop('The PC connection is too slow for live pen input. Start again after reconnecting.',true);return;}
      if (inFlight || (!queue.length && now()-sentAt < RENEW)) {arm();return;}
      const token=generation, capturedLease=lease, sentSequence=++sequence;
      const samples=queue.splice(0,batchLimit).map(item => item.sample);
      const packet={operation:samples.length ? 'samples' : 'renew',lease:capturedLease,sequence:sentSequence,elapsedMs:Math.round(now()-startedAt),samples};
      sentAt=now(); inFlight=true; arm();
      try {
        const result=await deadline(Promise.resolve().then(() => request(packet,REQUEST_TIMEOUT)));
        if (token !== generation) return;
        if (!result || result.ok !== true || result.sequence !== sentSequence) throw new Error('The PC could not confirm pen input. Start a new pen session.');
        inFlight=false; arm();
      } catch (error) {
        if (token === generation) void stop(error?.message || 'The PC disconnected. Start a new pen session after reconnecting.',true);
      }
    }
    return Object.freeze({start,stop,enqueue,tick,snapshot,get active(){return state === 'active';},get busy(){return state === 'active' || state === 'starting';}});
  }
  const core=Object.freeze({createClient,makeSample,normalizeSample,isContact,constants:Object.freeze({MAX_BATCH,MAX_PENDING,MAX_AGE,TICK,RENEW,REQUEST_TIMEOUT})});
  if (typeof module === 'object' && module.exports) module.exports=core;
  if (typeof window === 'undefined' || typeof document === 'undefined') return;
  window.DeskPenTabletCore=core;

  let host, statusLine, targetLine, pad, stage, cursor, coordinates, hint, startButton, stopButton, fullscreenButton, exitButton, adminControls, targetSelect, enableButton, disableButton, refreshButton;
  let active=false, suspended=false, status=null, statusError='', statusTimer=null, refreshing=false, configBusy=false, targetEdited=false, targetStamp='', pointerId=null, fullscreen=false, pollingGeneration=0, layoutGeneration=0, resizeObserver;
  const element=(tag,className,text) => {const node=document.createElement(tag);if(className)node.className=className;if(text !== undefined)node.textContent=text;return node;};
  const button=(text,handler,className='quiet') => {const node=element('button',className,text);node.type='button';node.addEventListener('click',handler);return node;};
  const desktop=() => typeof desktopConsole !== 'undefined' && desktopConsole === true;
  const request=(path,body,timeout=REQUEST_TIMEOUT) => {if(typeof api !== 'function')return Promise.reject(new Error('The PC connection is unavailable.'));return api(path,body,timeout);};
  const client=createClient({request:(body,timeout)=>request('/api/pen',body,timeout),onChange:()=>{if (!client.active) releasePointer();render();}});
  function releasePointer() {
    const captured=pointerId;pointerId=null;
    if(captured !== null) {try {if(pad?.hasPointerCapture(captured))pad.releasePointerCapture(captured);}catch{}}
    if(cursor)cursor.hidden=true;
  }
  function stop(reason,error=false) {releasePointer();return client.stop(reason,error);}
  function sessionId() {
    const bytes=new Uint8Array(16);
    if(!window.crypto?.getRandomValues)throw new Error('Secure pen sessions are unavailable in this browser. Use the paired DeskWing app.');
    window.crypto.getRandomValues(bytes);return Array.from(bytes,value=>value.toString(16).padStart(2,'0')).join('');
  }
  function ready() {return active && !suspended && !document.hidden && !statusError && status?.supported === true && status?.enabled === true && !!status?.target && !status?.active;}
  function render() {
    if(!host)return;
    const info=client.snapshot(), running=client.active;
    startButton.disabled=!ready() || client.busy || refreshing || configBusy;
    startButton.textContent=info.state === 'starting' ? 'Connecting…' : 'Start pen session';
    stopButton.disabled=!client.busy;
    statusLine.textContent=info.state === 'error' || client.busy ? info.message : statusError || status?.error || (status?.supported === false ? 'Windows pen input is unavailable on this PC.' : !status?.enabled ? 'Enable pen input in DeskWing on your PC, then refresh.' : status?.active ? 'Another pen session is using this PC. Stop it before starting here.' : info.message);
    statusLine.classList.toggle('pen-tablet-error',info.state === 'error' || !!statusError || !!status?.error);
    targetLine.textContent=status?.target ? `${status.target.label} · ${status.target.width} × ${status.target.height}` : 'No Windows target selected';
    targetLine.title=targetLine.textContent;
    host.classList.toggle('pen-tablet-connected',running);
    pad.setAttribute('aria-label',`Windows pen tablet surface. ${running ? 'Active. Pen only; fingers are ignored.' : 'Stopped. Start a pen session to use your stylus.'}`);
    hint.textContent=running ? 'Look at your PC and draw here. Your palm is ignored.' : 'Your pen controls the selected Windows screen or window.';
    fullscreenButton.textContent=fullscreen ? 'Exit fullscreen' : 'Fullscreen';
    fullscreenButton.setAttribute('aria-pressed',String(fullscreen));
    refreshButton.disabled=refreshing || configBusy || info.state === 'starting';
    if(adminControls){enableButton.disabled=configBusy || refreshing || !targetSelect.value || status?.supported !== true;disableButton.disabled=configBusy || !status?.enabled;targetSelect.disabled=configBusy || client.busy;enableButton.textContent=configBusy ? 'Applying…' : status?.enabled ? 'Use this target' : 'Enable pen input';}
    resizePad();
  }
  function updateTargets(targets) {
    if(!targetSelect || !Array.isArray(targets))return;
    const safe=targets.filter(target=>target && typeof target.id==='string' && typeof target.label==='string');
    const stamp=JSON.stringify(safe.map(target=>[target.id,target.label]));
    if(stamp !== targetStamp){const previous=targetSelect.value;targetSelect.replaceChildren();const placeholder=element('option','','Choose a screen or window');placeholder.value='';targetSelect.append(placeholder);for(const target of safe){const option=element('option','',`${target.kind === 'window' ? 'Window' : 'Screen'}: ${target.label}`);option.value=target.id;targetSelect.append(option);}targetSelect.value=previous;targetStamp=stamp;}
    if(!targetEdited && status?.target)targetSelect.value=status.target.id;
  }
  async function refresh() {
    if(!active || suspended || document.hidden || refreshing)return;
    if(client.snapshot().state === 'starting'){clearTimeout(statusTimer);statusTimer=setTimeout(refresh,250);return;}
    const token=pollingGeneration;refreshing=true;render();
    try {
      const result=await request(desktop()?'/api/admin/pen':'/api/pen',undefined,2500);
      if(token !== pollingGeneration || !active)return;
      status=desktop()?result.status:result;statusError='';
      if(desktop())updateTargets(result.targets);
      if(client.active && (!status?.enabled || !status?.supported || !status?.active))void stop('The PC ended this pen session. Start again when it is ready.',true);
    } catch(error) {
      if(token === pollingGeneration){statusError=error?.message || 'PC disconnected. Reconnect, then refresh.';if(client.busy)void stop('PC disconnected. Reconnect, then start a new pen session.',true);}
    } finally {
      refreshing=false;render();
      if(active && !suspended && !document.hidden){clearTimeout(statusTimer);statusTimer=setTimeout(refresh,2000);}
    }
  }
  async function configure(enabled) {
    if(configBusy || !desktop())return;
    await stop('Pen session stopped while the PC target changes.');
    configBusy=true;render();
    try {const result=await request('/api/admin/pen',{enabled,targetId:targetSelect.value || status?.target?.id || ''},2500);status=result;statusError='';targetEdited=false;}
    catch(error){statusError=error?.message || 'Could not update Windows pen input.';}
    finally{configBusy=false;render();void refresh();}
  }
  async function start() {
    if(!ready() || client.busy)return;
    try {const result=await client.start(sessionId());if(result && (!active || suspended || document.hidden))void stop('Pen session stopped. Start again when you return.');}
    catch(error){statusError=error?.message || 'Could not start the pen session.';render();}
  }
  function showSample(sample) {
    if(!sample || !cursor)return;
    cursor.hidden=sample.phase === 'leave';cursor.style.left=`${sample.x*100}%`;cursor.style.top=`${sample.y*100}%`;cursor.classList.toggle('pen-tablet-contact',sample.phase === 'down' || sample.phase === 'move');
    coordinates.textContent=`X ${Math.round(sample.x*100)}% · Y ${Math.round(sample.y*100)}% · Pressure ${Math.round(sample.pressure*100)}%${sample.eraser?' · Eraser':sample.barrel?' · Pen button':''}`;
  }
  function sample(event,phase) {
    const value=makeSample(event,pad.getBoundingClientRect(),phase);
    if(value){showSample(value);client.enqueue(value);}
  }
  function pointerDown(event) {
    if(event.pointerType !== 'pen'){event.preventDefault();return;}
    event.preventDefault();
    if(!active || suspended || !client.active || document.hidden || pointerId !== null)return;
    if(!isContact(event)){sample(event,'hover');return;}
    pointerId=event.pointerId;
    try{pad.setPointerCapture(pointerId);}catch{void stop('The tablet could not keep pen contact. Start a new pen session.',true);return;}
    sample(event,'down');
  }
  function pointerMove(event) {
    if(event.pointerType !== 'pen' || !client.active || !active || suspended)return;
    event.preventDefault();
    if(pointerId !== null && pointerId !== event.pointerId)return;
    let coalesced=[];
    try{coalesced=typeof event.getCoalescedEvents==='function'?event.getCoalescedEvents():[];}catch{}
    function transition(entry){
      if(!client.active)return;
      if(pointerId===null){
        if(isContact(entry)){pointerId=event.pointerId;try{pad.setPointerCapture(pointerId);}catch{void stop('The tablet could not keep pen contact. Start a new pen session.',true);return;}sample(entry,'down');}
        else sample(entry,'hover');
      }else if(!isContact(entry)){sample(entry,'up');releasePointer();}
      else sample(entry,'move');
    }
    for(const entry of coalesced.slice(-64))transition({pointerType:'pen',clientX:entry.clientX,clientY:entry.clientY,pressure:finite(entry.pressure)?entry.pressure:event.pressure,tiltX:finite(entry.tiltX)?entry.tiltX:event.tiltX,tiltY:finite(entry.tiltY)?entry.tiltY:event.tiltY,buttons:Number.isInteger(entry.buttons)?entry.buttons:event.buttons,button:Number.isInteger(entry.button)?entry.button:event.button});
    transition(event);
  }
  function pointerUp(event) {
    if(event.pointerType !== 'pen' || pointerId !== event.pointerId)return;
    event.preventDefault();sample(event,'up');releasePointer();
  }
  function pointerInterrupted(event) {if(event.pointerType==='pen' && pointerId===event.pointerId)void stop('Pen contact was interrupted. Start a new pen session.');}
  function resizePad() {
    if(!stage || !pad || !active)return;
    const bounds=stage.getBoundingClientRect(), ratio=status?.target?.width>0 && status?.target?.height>0 ? status.target.width/status.target.height : 16/10;
    const width=Math.max(1,Math.min(bounds.width,bounds.height*ratio)), height=width/ratio;
    if(pointerId !== null && (Math.abs(pad.clientWidth-width)>2 || Math.abs(pad.clientHeight-height)>2))void stop('The pen surface changed size. Start a new pen session.');
    pad.style.width=`${width}px`;pad.style.height=`${height}px`;
  }
  async function setFullscreen(value) {
    const token=++layoutGeneration;
    if(!host || fullscreen===value)return;
    if(client.busy)await stop('Pen session stopped for the new layout. Start again when you are ready.');
    if(token!==layoutGeneration || value && !active)return;
    fullscreen=value;host.classList.toggle('pen-tablet-fullscreen',value);render();
    try {if(value && host.requestFullscreen)await host.requestFullscreen();else if(!value && document.fullscreenElement===host)await document.exitFullscreen();}catch{}
    if(token!==layoutGeneration && document.fullscreenElement===host){try{await document.exitFullscreen();}catch{}}
    resizePad();
  }
  function suspend() {suspended=true;pollingGeneration++;clearTimeout(statusTimer);void stop('Pen session stopped while DeskWing is in the background. Start again when you return.');}
  function resume() {suspended=false;if(active)void refresh();}
  function setActive(value) {
    if(!host)build();active=value===true;
    if(host)host.hidden=!active;document.body.classList.toggle('pen-tablet-mode',active);
    if(active){suspended=false;render();void refresh();}
    else{pollingGeneration++;clearTimeout(statusTimer);void stop('Pen session stopped. Start again when you are ready.');void setFullscreen(false);}
    return active;
  }
  function build() {
    if(host)return;
    host=document.getElementById('pen-tablet-workspace');if(!host)return;
    host.classList.add('pen-tablet-workspace');host.hidden=!active;
    const heading=element('div','pen-tablet-heading'), title=element('div');title.append(element('h2','','Pen tablet'),element('p','help','Use your tablet pen in Windows apps such as Microsoft Whiteboard and Photoshop.'));
    const actions=element('div','pen-tablet-actions');fullscreenButton=button('Fullscreen',()=>void setFullscreen(!fullscreen));exitButton=button('Back to controls',()=>{void stop('Pen session stopped.');if(typeof setWorkspaceMode==='function')void setWorkspaceMode('studio');else setActive(false);});actions.append(fullscreenButton,exitButton);heading.append(title,actions);
    host.append(heading);
    if(desktop()){
      adminControls=element('div','pen-tablet-admin');const label=element('label','','Windows target');targetSelect=element('select');targetSelect.setAttribute('aria-label','Windows target');targetSelect.addEventListener('change',()=>{targetEdited=true;render();});label.append(targetSelect);enableButton=button('Enable pen input',()=>void configure(true),'primary');disableButton=button('Disable pen input',()=>void configure(false),'danger');
      adminControls.append(label,enableButton,disableButton,element('p','help','Choose a screen, or keep a selected app window in the foreground. Permission resets when DeskWing closes. Paired tablets can start one pen session at a time.'));host.append(adminControls);
    }
    const connection=element('div','pen-tablet-connection'), details=element('div','pen-tablet-target');targetLine=element('strong','','No Windows target selected');statusLine=element('p','pen-tablet-status','Checking the PC connection…');statusLine.setAttribute('role','status');details.append(targetLine,statusLine);
    const sessionActions=element('div','pen-tablet-actions');refreshButton=button('Refresh',()=>void refresh());startButton=button('Start pen session',()=>void start(),'primary');stopButton=button('Stop pen',()=>void stop('Pen session stopped. Your Windows app is no longer controlled by this tablet.'),'danger');sessionActions.append(refreshButton,startButton,stopButton);connection.append(details,sessionActions);host.append(connection);
    stage=element('div','pen-tablet-stage');pad=element('div','pen-tablet-pad');pad.tabIndex=0;pad.setAttribute('role','application');hint=element('p','pen-tablet-hint');cursor=element('span','pen-tablet-cursor');cursor.hidden=true;cursor.setAttribute('aria-hidden','true');pad.append(hint,cursor);stage.append(pad);host.append(stage);
    const footer=element('div','pen-tablet-footer');coordinates=element('output','','Pen only · palm touches ignored');footer.append(coordinates,element('p','help','This surface maps to your chosen PC target. Artwork stays in the Windows app. Use Windows Ink in apps that offer a pen input setting.'));host.append(footer);
    pad.addEventListener('pointerdown',pointerDown);pad.addEventListener('pointermove',pointerMove);pad.addEventListener('pointerup',pointerUp);pad.addEventListener('pointercancel',event=>{if(event.pointerType==='pen' && client.active && (pointerId===null || pointerId===event.pointerId))void stop('Pen contact was interrupted. Start a new pen session.');});pad.addEventListener('lostpointercapture',pointerInterrupted);pad.addEventListener('pointerleave',event=>{if(event.pointerType==='pen' && pointerId===null && client.active)sample(event,'leave');});pad.addEventListener('contextmenu',event=>event.preventDefault());
    if(typeof ResizeObserver==='function'){resizeObserver=new ResizeObserver(resizePad);resizeObserver.observe(stage);}
    render();
  }
  window.DeskPenTablet=Object.freeze({setActive,isActive:()=>active,isBusy:()=>client.busy,suspend,resume});
  window.penTabletSuspend=suspend;window.penTabletResume=resume;
  document.addEventListener('DOMContentLoaded',build);
  document.addEventListener('visibilitychange',()=>document.hidden?suspend():resume());
  document.addEventListener('fullscreenchange',()=>{if(fullscreen && document.fullscreenElement!==host){fullscreen=false;host?.classList.remove('pen-tablet-fullscreen');void stop('Fullscreen ended. Start a new pen session when you are ready.');render();}});
  window.addEventListener('blur',suspend);window.addEventListener('focus',resume);window.addEventListener('pagehide',suspend);window.addEventListener('resize',resizePad);
  window.addEventListener('keydown',event=>{if(active && event.key==='Escape'){void stop('Pen session stopped.');if(fullscreen)void setFullscreen(false);}});
  if(document.readyState!=='loading')build();
})();
