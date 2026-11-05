'use strict';
// Rendered shared ink and Windows pen workflows. Every API and native bridge call is intercepted.
// CDP pen/touch events exercise Chromium pointer handling; no physical stylus or OS input is used.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'..'),webroot=fs.realpathSync(path.join(root,'companion/wwwroot'));
const output=process.env.TABLETDECK_TEST_OUTPUT?path.join(path.resolve(process.env.TABLETDECK_TEST_OUTPUT),'ink-rendered'):path.join(root,'.impeccable/review');
const captureOnly=new Set((process.env.TABLETDECK_CAPTURE_ONLY||'').split(',').filter(Boolean));
const origin='http://127.0.0.1:18997',target={id:'fixture-screen',kind:'screen',label:'Studio display',width:1920,height:1080};
const targets=[target,{id:'fixture-whiteboard',kind:'window',label:'Microsoft Whiteboard',width:1600,height:900}];
let browser,checks=0,inkRevision=0,penSequence=0,lease='',enabled=false,currentTarget=target;
let previewJpeg='',frameSequence=0,frameInflight=0,maxFrameInflight=0,holdFrame=false;
const pendingFrames=[],frameRequests=[];
let sharedInk={workspaceId:'rendered-shared-ink',revision:'0'.repeat(64),document:{version:1,strokes:[],notes:'',penOnly:true,updatedAt:'2026-11-05T00:00:00.000Z'}};
const failures=[],errors=[],unexpected=[],penPackets=[],inkRequests=[],captures=[];
const check=(value,label)=>{checks++;if(value)console.log('PASS '+label);else{failures.push(label);console.error('FAIL '+label);}};
const status=()=>({supported:true,enabled,active:!!lease,target:currentTarget,error:''});
const board={profileId:'fixture-profile',profileName:'Studio',pages:[{id:'main',name:'Main',icon:'grid',buttons:[{id:'scene',label:'Gameplay',kind:'scene',target:'Gameplay',icon:'gamepad',color:'violet',configured:true}]}]};
async function fixture(route){
 const request=route.request(),url=new URL(request.url());if(url.origin!==origin){unexpected.push('External '+url.origin);return route.abort();}
 const send=(value,code=200)=>route.fulfill({status:code,contentType:'application/json',body:JSON.stringify(value)});
 if(url.pathname.startsWith('/api/')){
  switch(url.pathname){
   case '/api/board':return send(board);
   case '/api/state':return send({pcConnected:true,obsConnected:false,inputs:{},session:'ink-fixture',revision:1,execution:{session:'ink-fixture',revision:1,active:null}});
   case '/api/execution':return send({session:'ink-fixture',revision:1,active:null});
   case '/api/events':return route.fulfill({status:200,contentType:'text/event-stream',body:': Fixture only.\n\n'});
   case '/api/profile-lock':return send({ok:true});
   case '/api/profiles':return send({profiles:[{id:board.profileId,name:board.profileName}]});
   case '/api/admin/info':return send({devices:[{id:'tablet'}],addresses:['192.0.2.10'],lanAddresses:['192.0.2.10'],connectedTablets:1});
   case '/api/admin/desktop':return send({usbStatus:'Tablet connected · fixture',preferences:{}});
   case '/api/ink':{
    inkRequests.push({method:request.method(),revision:url.searchParams.get('revision')});
    if(request.method()==='GET')return send(url.searchParams.get('revision')===sharedInk.revision?{unchanged:true,workspaceId:sharedInk.workspaceId,revision:sharedInk.revision}:sharedInk);
    const body=request.postDataJSON();if(body.expectedRevision!==sharedInk.revision)return send({error:'Ink changed on another device',code:'ink_conflict',revision:sharedInk.revision},409);
    sharedInk={...sharedInk,revision:(++inkRevision).toString(16).padStart(64,'0'),document:structuredClone(body.document)};return send(sharedInk);
   }
   case '/api/admin/pen':{
    if(request.method()==='GET')return send({status:status(),targets});
    const body=request.postDataJSON();enabled=body.enabled===true;currentTarget=targets.find(item=>item.id===body.targetId)||target;lease='';return send(status());
   }
   case '/api/pen/frame':{
    const body=request.postDataJSON();if(request.method()!=='POST'||body.lease!==lease||!lease)return send({error:'Pen preview requires the active lease'},409);
    frameRequests.push(structuredClone(body));frameInflight++;maxFrameInflight=Math.max(maxFrameInflight,frameInflight);
    const selected=structuredClone(currentTarget),sequence=++frameSequence;
    try{if(holdFrame)await new Promise(resolve=>pendingFrames.push(resolve));return await send({image:previewJpeg,width:640,height:360,targetId:selected.id,targetWidth:selected.width,targetHeight:selected.height,sequence,capturedAt:new Date().toISOString()});}finally{frameInflight--;}
   }
   case '/api/pen':{
    if(request.method()==='GET')return send(status());
    const body=request.postDataJSON();penPackets.push(structuredClone(body));
    if(body.operation==='begin'){if(!enabled||lease)return send({error:'Pen is disabled or busy'},409);lease='fixture-lease-'+body.session;penSequence=0;return send({lease,sequence:0,maxBatch:32,target:structuredClone(currentTarget)});}
    if(body.lease!==lease)return send({error:'Pen lease expired'},409);
    if(['cancel','release','end'].includes(body.operation)){lease='';return send({ok:true});}
    if(!['renew','samples'].includes(body.operation)||body.sequence!==penSequence+1)return send({error:'Invalid fixture sequence'},409);
    penSequence=body.sequence;return send({ok:true,sequence:penSequence});
   }
   default:unexpected.push(request.method()+' '+url.pathname);return send({error:'Unexpected fixture API'},404);
  }
 }
 let relative;try{relative=decodeURIComponent(url.pathname).replace(/^\/+/, '')||'index.html';}catch{return route.fulfill({status:400,body:''});}
 const file=path.resolve(webroot,relative),inside=path.relative(webroot,file);
 if(!inside||path.isAbsolute(inside)||inside==='..'||inside.startsWith('..'+path.sep)||!fs.existsSync(file)||!fs.statSync(file).isFile())return route.fulfill({status:404,body:''});
 const real=path.relative(webroot,fs.realpathSync(file));if(path.isAbsolute(real)||real==='..'||real.startsWith('..'+path.sep))return route.fulfill({status:404,body:''});
 return route.fulfill({status:200,contentType:{'.html':'text/html','.css':'text/css','.js':'text/javascript','.svg':'image/svg+xml','.ttf':'font/ttf'}[path.extname(file)]||'application/octet-stream',body:fs.readFileSync(file)});
}
async function pageFor(desktop,search=''){
 const context=await browser.newContext({viewport:{width:1280,height:800},hasTouch:!desktop});await context.route('**/*',fixture);
 if(!desktop)await context.addInitScript(()=>{
  window.fixtureNativeRequests=[];window.DeckNative={dispatch(text){const request=JSON.parse(text);window.fixtureNativeRequests.push(request);
   const response=request.path==='/native/connection'?Promise.resolve({paired:true,mode:'usb',host:'127.0.0.1'}):request.path==='/local/handwriting'?Promise.resolve({cancelled:true}):request.path==='/local/fullscreen'?Promise.resolve({enabled:request.body.enabled===true}):request.path.startsWith('/api/')?fetch(request.path,{method:request.method,headers:{'Content-Type':'application/json'},body:request.body===undefined?undefined:JSON.stringify(request.body)}).then(response=>response.json()):Promise.resolve({error:'Unexpected fixture native request'});
   response.then(value=>window.nativeResult(request.id,value)).catch(error=>window.nativeResult(request.id,{error:error.message}));
  }};
 });
 const page=await context.newPage();page.setDefaultTimeout(10000);page.on('pageerror',error=>errors.push((desktop?'desktop: ':'tablet: ')+error.message));
 await page.goto(origin+'/'+(desktop?'desktop.html'+search+'#admin=fixture':'index.html'));await page.locator(search?'#pen-canvas':desktop?'#console-board':'.key').first().waitFor();return page;
}
async function mode(page,value){await page.locator('#app-mode').selectOption(value);await page.waitForFunction(value=>DeskWorkspace.mode===value,value);}
async function settle(page){await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));}
async function previewVisible(page,label){
 await page.waitForFunction(()=>{const image=document.querySelector('.pen-tablet-preview');return image&&!image.hidden&&image.complete&&image.naturalWidth>0;});
 const geometry=await page.locator('.pen-tablet-preview').evaluate(image=>{const box=image.getBoundingClientRect(),pad=image.closest('.pen-tablet-pad'),bounds=pad.getBoundingClientRect(),hit=document.elementFromPoint(bounds.x+bounds.width*.5,bounds.y+bounds.height*.5);return{fills:Math.abs(box.width-bounds.width)<=2&&Math.abs(box.height-bounds.height)<=2,pointerSafe:getComputedStyle(image).pointerEvents==='none'&&hit===pad,decoded:image.naturalWidth===640&&image.naturalHeight===360};});
 check(geometry.decoded&&geometry.fills,label+' displays the decoded target preview across its input surface');check(geometry.pointerSafe,label+' keeps the preview behind the pen input hit target');
}
async function previewCleared(page,label){await page.waitForFunction(()=>{const image=document.querySelector('.pen-tablet-preview');return image&&image.hidden&&!image.getAttribute('src');});check(true,label+' clears the preview image and source immediately');}
async function sync(page){const compact=await page.locator('#ink-window-tools').count()>0&&await page.locator('#pen-sync-now').isHidden();if(compact)await page.locator('#ink-window-tools').click();await page.locator('#pen-sync-now').click();await page.waitForFunction(()=>!document.querySelector('#pen-sync-now').disabled&&document.querySelector('#pen-sync-state').textContent==='Shared with your PC');if(compact)await page.locator('#ink-window-tools').click();}
async function screenshot(page,name){if(captureOnly.size===0||captureOnly.has(name))await page.screenshot({path:path.join(output,name+'.png'),fullPage:true});}
async function penStroke(page,selector,points){
 await page.locator(selector).scrollIntoViewIfNeeded();const box=await page.locator(selector).boundingBox(),cdp=await page.context().newCDPSession(page);
 const sample=async(type,point,pressure)=>cdp.send('Input.dispatchMouseEvent',{type,x:box.x+box.width*point[0],y:box.y+box.height*point[1],button:type==='mouseMoved'?'none':'left',buttons:type==='mouseReleased'?0:1,clickCount:type==='mouseMoved'?0:1,pointerType:'pen',force:pressure,tiltX:12,tiltY:-8});
 await sample('mousePressed',points[0],.3);for(const point of points.slice(1))await sample('mouseMoved',point,.75);await sample('mouseReleased',points.at(-1),0);await cdp.detach();
}
async function palm(page,selector){
 const box=await page.locator(selector).boundingBox(),cdp=await page.context().newCDPSession(page);
 const touches=[{x:box.x+box.width*.3,y:box.y+box.height*.5,radiusX:30,radiusY:22,force:.7,id:1},{x:box.x+box.width*.5,y:box.y+box.height*.6,radiusX:32,radiusY:24,force:.6,id:2}];
 await cdp.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:touches});await cdp.send('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:touches.map(point=>({...point,x:point.x+12}))});await cdp.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});await cdp.detach();
}
async function visibleControl(page,locator,label){
 const visible=await locator.evaluate(node=>{const box=node.getBoundingClientRect(),hit=document.elementFromPoint(box.x+box.width/2,box.y+box.height/2);return box.height>=44&&box.width>=44&&box.left>=0&&box.top>=0&&box.right<=innerWidth+1&&box.bottom<=innerHeight+1&&(hit===node||node.contains(hit));});
 check(visible,label+' is a visible, unobscured 44px control');
}
async function capture(page,name,host,pad,controls){
 await page.locator(host).evaluate(node=>node.scrollTop=0);await settle(page);
 const geometry=await page.evaluate(({host,pad})=>{const area=document.querySelector(host),a=area.getBoundingClientRect(),p=document.querySelector(pad).getBoundingClientRect();return{width:innerWidth,height:innerHeight,noHorizontal:document.documentElement.scrollWidth<=innerWidth+1&&area.scrollWidth<=area.clientWidth+1,padFits:p.width>100&&p.height>70&&p.left>=a.left&&p.right<=a.right+1&&p.top>=a.top&&p.bottom<=Math.min(a.bottom,innerHeight)+1,hostFits:a.top>=0&&a.bottom<=innerHeight+1};},{host,pad});
 check(geometry.noHorizontal,name+' has no horizontal clipping');check(geometry.padFits,name+' keeps its entire drawing surface inside the viewport');
 for(const [locator,label]of controls)await visibleControl(page,locator,name+' '+label);
 await screenshot(page,name);captures.push({name,...geometry});
}
async function run(){
 fs.mkdirSync(output,{recursive:true});browser=await chromium.launch({headless:true,executablePath:process.env.TABLETDECK_CHROME||'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 const desktop=await pageFor(true),tablet=await pageFor(false);await mode(desktop,'pen');await mode(tablet,'pen');await sync(desktop);await sync(tablet);
 previewJpeg=await desktop.evaluate(()=>{const canvas=document.createElement('canvas');canvas.width=960;canvas.height=540;const paint=canvas.getContext('2d');paint.fillStyle='#f6f3e9';paint.fillRect(0,0,960,540);paint.fillStyle='#17232d';paint.fillRect(0,0,960,50);paint.fillStyle='#ecf5f7';paint.font='18px sans-serif';paint.fillText('PC drawing preview · synthetic test fixture',24,32);paint.strokeStyle='#d8ddd9';paint.lineWidth=1;for(let x=24;x<960;x+=40){paint.beginPath();paint.moveTo(x,68);paint.lineTo(x,520);paint.stroke();}for(let y=68;y<540;y+=40){paint.beginPath();paint.moveTo(16,y);paint.lineTo(944,y);paint.stroke();}paint.strokeStyle='#285e7e';paint.lineWidth=6;paint.lineCap='round';paint.beginPath();paint.moveTo(95,380);paint.lineTo(250,195);paint.lineTo(390,330);paint.lineTo(570,135);paint.lineTo(755,275);paint.stroke();paint.strokeStyle='#b35355';paint.lineWidth=5;paint.beginPath();paint.arc(570,135,42,0,Math.PI*2);paint.stroke();paint.beginPath();paint.moveTo(110,455);paint.lineTo(815,455);paint.lineTo(790,432);paint.moveTo(815,455);paint.lineTo(790,478);paint.stroke();paint.fillStyle='#344c5e';paint.font='20px sans-serif';paint.fillText('Sketch, refine, share',325,505);const reduced=document.createElement('canvas');reduced.width=640;reduced.height=360;reduced.getContext('2d').drawImage(canvas,0,0,640,360);return reduced.toDataURL('image/jpeg',.85);});
 check(await desktop.evaluate(()=>JSON.parse(localStorage.getItem('deskwing.pen.v1')||'{"strokes":[]}').strokes.length)===0,'PC begins with an empty shared scratchpad');
 await tablet.locator('#pen-notes').fill('Shared ideas\nSketch on the tablet. Review on the PC.');
 await penStroke(tablet,'#pen-canvas',[[.13,.66],[.28,.36],[.44,.62],[.61,.25],[.82,.47]]);
 await tablet.waitForFunction(()=>JSON.parse(localStorage.getItem('deskwing.pen.v1')||'{}').strokes?.length===1);await sync(tablet);await sync(desktop);
 check(await desktop.locator('#pen-notes').inputValue()==='Shared ideas\nSketch on the tablet. Review on the PC.','PC receives tablet notes through the shared ink API');
 check(await desktop.evaluate(()=>JSON.parse(localStorage.getItem('deskwing.pen.v1')).strokes.length)===1,'PC receives the tablet pen stroke in its separate context');
 await settle(desktop);check(await desktop.locator('#pen-canvas').evaluate(canvas=>canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data.some((value,index)=>index%4===3&&value>0)),'PC canvas redraws the received stroke');
 const beforePalm=sharedInk.document.strokes.length,postsBeforePalm=penPackets.length;await palm(tablet,'#pen-canvas');await sync(tablet);
 check(sharedInk.document.strokes.length===beforePalm&&penPackets.length===postsBeforePalm,'scratchpad palm contacts add no strokes and make zero Windows pen calls');
 await desktop.locator('#pen-notes').fill('Reviewed on the PC. Keep the next sketch simple.');await sync(desktop);await sync(tablet);check(await tablet.locator('#pen-notes').inputValue()==='Reviewed on the PC. Keep the next sketch simple.','tablet receives the PC note edit without losing its stroke');
 check(inkRequests.some(request=>/^[0-9a-f]{64}$/.test(request.revision||'')),'shared ink polls with the last known revision');
 await screenshot(desktop,'desktop-shared-ink');
 const inkWindow=await pageFor(true,'?workspace=ink-window');await sync(inkWindow);
 check(await inkWindow.title()==='DeskWing Ink'&&await inkWindow.locator('.console-sidebar').isHidden()&&await inkWindow.locator('.console-header').isHidden(),'dedicated Ink window opens shared ink without companion navigation');
 for(const [width,height,name]of [[1000,740,'desktop-ink-window'],[900,640,'desktop-ink-window-small'],[620,480,'desktop-ink-window-minimum'],[604,441,'desktop-ink-window-client-minimum']]){
  await inkWindow.setViewportSize({width,height});await inkWindow.locator('#pen-workspace').evaluate(node=>node.scrollTop=0);await settle(inkWindow);
  const bounds=await inkWindow.evaluate(()=>{const host=document.querySelector('#pen-workspace'),box=document.querySelector('#pen-canvas').getBoundingClientRect();return{width:innerWidth,height:innerHeight,canvasWidth:box.width,canvasHeight:box.height,fit:box.left>=-1&&box.top>=-1&&box.right<=innerWidth+1&&box.bottom<=innerHeight+1,noScroll:document.documentElement.scrollWidth<=innerWidth+1&&host.scrollWidth<=host.clientWidth+1&&host.scrollHeight<=host.clientHeight+1,ratio:box.width/box.height};});
  check(bounds.noScroll,name+' keeps its default canvas view free of horizontal and vertical scrolling');check(bounds.fit&&bounds.canvasHeight>=200&&Math.abs(bounds.ratio-1.6)<.01,name+' fits the whole 16:10 paper with at least 200px canvas height');
  check(await inkWindow.locator('#pen-toolbox').isHidden()&&await inkWindow.locator('.pen-notes').isHidden()&&!await inkWindow.evaluate(()=>DeskPen.isFullscreen()),name+' opens with tools and notes closed in an ordinary window');
  await visibleControl(inkWindow,inkWindow.locator('#pen-fullscreen'),name+' Fullscreen');await visibleControl(inkWindow,inkWindow.locator('#ink-window-tools'),name+' Show tools');await screenshot(inkWindow,name);captures.push({name,...bounds});
  await inkWindow.locator('#ink-window-tools').click();await inkWindow.locator('#pen-tool').click({trial:true});check(await inkWindow.locator('#pen-toolbox').evaluate(node=>{const color=getComputedStyle(node).backgroundColor;return color!=='transparent'&&color!=='rgba(0, 0, 0, 0)';}),name+' tools overlay has an opaque theme background');await screenshot(inkWindow,name+'-tools');await inkWindow.locator('#ink-window-notes').click();await inkWindow.locator('#pen-notes').click({trial:true});check(await inkWindow.locator('#pen-notes').inputValue()==='Reviewed on the PC. Keep the next sketch simple.',name+' tools and notes remain reachable without losing shared text');check(await inkWindow.locator('.pen-notes').evaluate(node=>{const color=getComputedStyle(node).backgroundColor;return color!=='transparent'&&color!=='rgba(0, 0, 0, 0)';}),name+' notes overlay has an opaque theme background');await screenshot(inkWindow,name+'-notes');await inkWindow.locator('#ink-window-hide-notes').click();
  check(await inkWindow.locator('#pen-toolbox').isHidden()&&await inkWindow.locator('.pen-notes').isHidden(),name+' dismisses overlays back to the full canvas');
 }
 await inkWindow.context().close();
 for(const [width,height,name] of [[1280,800,'tablet-scratchpad-fullscreen'],[668,1070,'tablet-scratchpad-fullscreen-portrait'],[390,844,'tablet-scratchpad-fullscreen-narrow']]){
  await tablet.setViewportSize({width,height});await tablet.locator('#pen-fullscreen').click();await tablet.waitForFunction(()=>DeskPen.isFullscreen());await settle(tablet);
  check(await tablet.locator('#pen-toolbox').isHidden()&&await tablet.locator('.topbar').isHidden(),name+' hides toolbar and app chrome by default');
  check(await tablet.evaluate(()=>window.fixtureNativeRequests.filter(request=>request.path==='/local/fullscreen').at(-1)?.body.enabled===true),name+' requests native immersive system bars');
  await capture(tablet,name,'#pen-workspace','#pen-canvas',[[tablet.locator('#pen-fullscreen'),'Exit fullscreen'],[tablet.locator('#pen-fullscreen-tools'),'Show tools']]);
  await tablet.locator('#pen-fullscreen-tools').click();await visibleControl(tablet,tablet.locator('#pen-fullscreen'),name+' Exit with tools open');await screenshot(tablet,name+'-tools');
  await tablet.locator('#pen-fullscreen-notes').click();await tablet.locator('#pen-fullscreen-tools').click();await capture(tablet,name+'-notes','#pen-workspace','#pen-canvas',[[tablet.locator('#pen-fullscreen'),'Exit fullscreen'],[tablet.locator('#pen-fullscreen-tools'),'Show tools']]);
  check(await tablet.locator('#pen-notes').isVisible(),name+' can reveal editable notes');await tablet.locator('#pen-fullscreen-tools').click();await tablet.locator('#pen-fullscreen-notes').click();await tablet.locator('#pen-fullscreen').click();await tablet.waitForFunction(()=>!DeskPen.isFullscreen());
  check(await tablet.evaluate(()=>window.fixtureNativeRequests.filter(request=>request.path==='/local/fullscreen').at(-1)?.body.enabled===false),name+' restores native system bars on exit');
 }
 await desktop.setViewportSize({width:1280,height:800});await mode(desktop,'pentablet');await desktop.getByRole('combobox',{name:'Windows target',exact:true}).selectOption('fixture-screen');await desktop.getByRole('button',{name:'Enable pen & live view',exact:true}).click();await desktop.getByRole('button',{name:'Disable pen & live view',exact:true}).waitFor();
 check(enabled&&await desktop.locator('#console-title').innerText()==='Pen tablet','desktop target settings explicitly enable pen input and retain the correct header');await screenshot(desktop,'desktop-pen-tablet-settings');
 await tablet.setViewportSize({width:1280,height:800});await mode(tablet,'pentablet');await tablet.getByRole('button',{name:'Start pen session',exact:true}).waitFor();
 for(const [width,height,name]of [[1280,800,'tablet-pen-tablet-fullscreen'],[668,1070,'tablet-pen-tablet-fullscreen-portrait'],[390,844,'tablet-pen-tablet-fullscreen-narrow']]){
  await tablet.setViewportSize({width,height});await tablet.getByRole('button',{name:'Fullscreen',exact:true}).click();await tablet.locator('.pen-tablet-fullscreen').waitFor();const floating=tablet.locator('.pen-tablet-floating-controls');await floating.getByRole('button',{name:'Start pen',exact:true}).click();await tablet.locator('.pen-tablet-connected').waitFor();
  check(await tablet.locator('#pen-tablet-fullscreen-tools').isHidden()&&await tablet.locator('.pen-tablet-heading').isHidden(),name+' hides status and toolbar until requested');
  check(await tablet.locator('.pen-tablet-pad').evaluate(node=>{const box=node.getBoundingClientRect();return Math.abs(box.width-innerWidth)<=1&&Math.abs(box.height-innerHeight)<=1;}),name+' maps the full tablet surface by default');
  await previewVisible(tablet,name+' Fill');
  await capture(tablet,name,'#pen-tablet-workspace','.pen-tablet-pad',[[floating.getByRole('button',{name:'Exit fullscreen',exact:true}),'Exit fullscreen'],[floating.getByRole('button',{name:'Stop pen',exact:true}),'Stop pen'],[floating.getByRole('button',{name:'Show tools',exact:true}),'Show tools']]);
  await floating.getByRole('button',{name:'Stop pen',exact:true}).click();await tablet.waitForFunction(()=>!DeskPenTablet.isBusy());await previewCleared(tablet,name+' Stop');await floating.getByRole('button',{name:'Show tools',exact:true}).click();await tablet.getByRole('combobox',{name:'Surface mapping',exact:true}).selectOption('match');await settle(tablet);
  check(await tablet.locator('.pen-tablet-pad').evaluate(node=>{const box=node.getBoundingClientRect();return Math.abs(box.width/box.height-16/9)<.01;}),name+' optionally matches PC proportions');await screenshot(tablet,name+'-tools');await floating.getByRole('button',{name:'Start pen',exact:true}).click();await previewVisible(tablet,name+' Match');await capture(tablet,name+'-match','#pen-tablet-workspace','.pen-tablet-pad',[[floating.getByRole('button',{name:'Exit fullscreen',exact:true}),'Exit fullscreen'],[floating.getByRole('button',{name:'Stop pen',exact:true}),'Stop pen']]);await floating.getByRole('button',{name:'Stop pen',exact:true}).click();await previewCleared(tablet,name+' Match stop');await floating.getByRole('button',{name:'Show tools',exact:true}).click();await tablet.getByRole('combobox',{name:'Surface mapping',exact:true}).selectOption('fill');
  await floating.getByRole('button',{name:'Exit fullscreen',exact:true}).click();await tablet.locator('.pen-tablet-fullscreen').waitFor({state:'detached'});check(await tablet.evaluate(()=>window.fixtureNativeRequests.filter(request=>request.path==='/local/fullscreen').at(-1)?.body.enabled===false),name+' restores native system bars on exit');
 }
 await tablet.setViewportSize({width:1280,height:800});await settle(tablet);await tablet.getByRole('button',{name:'Start pen session',exact:true}).click();await tablet.waitForFunction(()=>DeskPenTablet.isBusy()).catch(async error=>{console.error(JSON.stringify({penStatus:await tablet.locator('.pen-tablet-status').innerText(),previewStatus:await tablet.locator('.pen-tablet-preview-status').innerText(),leaseActive:!!lease,lastOperations:penPackets.slice(-4).map(packet=>packet.operation)}));throw error;});await previewVisible(tablet,'Windowed session');
 const callsBeforePalm=penPackets.length;await palm(tablet,'.pen-tablet-pad');await settle(tablet);check(penPackets.length===callsBeforePalm,'palm contacts on active Pen tablet produce zero native pen calls');
 holdFrame=true;const holdDeadline=Date.now()+3000;while(!pendingFrames.length&&Date.now()<holdDeadline)await new Promise(resolve=>setTimeout(resolve,10));assert.ok(pendingFrames.length,'a preview frame is intentionally pending');const samplePacketsBefore=penPackets.filter(packet=>packet.operation==='samples').length;
 await penStroke(tablet,'.pen-tablet-pad',[[.2,.6],[.4,.35],[.6,.5]]);await tablet.waitForFunction(()=>document.querySelector('.pen-tablet-footer output').textContent.includes('Pressure'));
 await new Promise(resolve=>setTimeout(resolve,80));const samples=penPackets.filter(packet=>packet.operation==='samples').flatMap(packet=>packet.samples);
 check(samples.some(sample=>sample.phase==='down')&&samples.some(sample=>sample.phase==='up')&&samples.some(sample=>sample.pressure>0),'trusted Chromium pen events produce bounded pressure samples only in the fixture');
 check(pendingFrames.length===1&&penPackets.filter(packet=>packet.operation==='samples').length>samplePacketsBefore,'pen packets continue while the preview frame response is blocked');check(maxFrameInflight===1,'preview capture never overlaps another frame request');
 const stopsBefore=penPackets.filter(packet=>packet.operation==='cancel').length;await mode(tablet,'pen');await tablet.waitForFunction(()=>!DeskPenTablet.isBusy());
 check(!lease&&penPackets.filter(packet=>packet.operation==='cancel').length===stopsBefore+1,'changing workspace cancels the native pen session exactly once');
 await previewCleared(tablet,'Workspace change');const lateResponse=tablet.waitForResponse(response=>new URL(response.url()).pathname==='/api/pen/frame');holdFrame=false;pendingFrames.shift()();await lateResponse;await settle(tablet);await previewCleared(tablet,'Late frame after workspace change');
 check(await tablet.locator('#pen-notes').inputValue()==='Reviewed on the PC. Keep the next sketch simple.','returning from Pen tablet preserves shared scratchpad notes');
 check(unexpected.length===0,'all requests stay within known fixture routes: '+unexpected.join(', '));check(errors.length===0,'shared ink and Pen tablet have no browser JavaScript errors: '+errors.join('; '));
 fs.writeFileSync(path.join(output,'ink-rendered-results.json'),JSON.stringify({checks,failures,errors,unexpected,captures,inkRevision,penPackets,frameRequests,maxFrameInflight,syntheticPreview:true,fixtureOnly:true,physicalPenTested:false,osInputInjected:false,nativeHandwritingTested:false},null,2));
 assert.equal(failures.length,0,failures.join('\n'));console.log(checks+' ink checks passed; captures in '+output);
}
run().catch(error=>{console.error(error);process.exitCode=1;}).finally(async()=>{for(const resolve of pendingFrames)resolve();await browser?.close();});
