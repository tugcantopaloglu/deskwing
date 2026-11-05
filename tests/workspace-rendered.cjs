'use strict';
// Actual shared documents, intercepted local APIs only. No service, provider, media or PC action.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'..'),webroot=fs.realpathSync(path.join(root,'companion/wwwroot'));
const output=process.env.TABLETDECK_TEST_OUTPUT?path.join(path.resolve(process.env.TABLETDECK_TEST_OUTPUT),'workspace-rendered'):path.join(root,'.impeccable/review'),origin='http://127.0.0.1:18998';
const captureOnly=new Set((process.env.TABLETDECK_CAPTURE_ONLY||'').split(',').filter(Boolean));
let browser,checks=0,pendingSave=null,holdSave=false,saveCount=0;
const emptyInk=()=>({workspaceId:'fixture-ink',revision:'0'.repeat(64),document:{version:1,strokes:[],notes:'',penOnly:true,updatedAt:'2026-11-05T00:00:00.000Z'}});
let sharedInk=emptyInk(),inkRevision=0;
const penTarget={id:'fixture-screen',kind:'screen',label:'Fixture display',width:1920,height:1080};
const penStatus={supported:true,enabled:false,active:false,target:penTarget};
const errors=[],failures=[],unhandled=[],controlRequests=[],captures=[],externalRequests=[];
const check=(value,label)=>{checks++;if(!value){failures.push(label);console.error('FAIL '+label);}else console.log('PASS '+label);};
const labels=['Starting soon','Gameplay','Just chatting','Short break','Microphone','Desktop audio','Replay clip','Camera','Applause','Discord','Studio lights','End stream'];
let saved={profileId:'review-profile',profileName:'Studio essentials',boardRevision:'review-1',pages:[{id:'main',name:'Live studio',icon:'grid',buttons:labels.map((label,i)=>({id:'key-'+i,label,kind:'scene',target:label,value:'',icon:['clapper','gamepad','chat','coffee','mic','speaker','replay','camera','clap','discord','bolt','stop'][i],color:['violet','violet','violet','violet','blue','blue','amber','blue','amber','blue','blue','red'][i],confirm:i===11,steps:[]}))},{id:'work',name:'Work & create',icon:'monitor',buttons:[]}]};
const settings={enabled:true,automatic:false,manualPresetId:'coding',presets:[{id:'coding',label:'Code & create',applications:['Code.exe'],controls:[{id:'save',label:'Save file',group:'File',shortcut:'CTRL+S',global:false},{id:'find',label:'Find',group:'Navigate',shortcut:'CTRL+F',global:false},{id:'undo',label:'Undo',group:'Edit',shortcut:'CTRL+Z',global:false}]},{id:'photoshop',label:'Photoshop',applications:['Photoshop.exe'],controls:[]}]};
async function routeFixture(route){
 const request=route.request(),url=new URL(request.url());
 if(url.origin!==origin){externalRequests.push(url.origin);return route.abort();}
 const send=(value,status=200)=>route.fulfill({status,contentType:'application/json',body:JSON.stringify(value)});
 if(url.pathname.startsWith('/api/')){
  switch(url.pathname){
   case '/api/ink':{
    if(request.method()==='GET')return send(url.searchParams.get('revision')===sharedInk.revision?{unchanged:true,workspaceId:sharedInk.workspaceId,revision:sharedInk.revision}:sharedInk);
    const body=request.postDataJSON();if(body.expectedRevision!==sharedInk.revision)return send({error:'Ink revision changed',code:'ink_conflict'},409);
    sharedInk={...sharedInk,revision:(++inkRevision).toString(16).padStart(64,'0'),document:structuredClone(body.document)};return send(sharedInk);
   }
   case '/api/pen':if(request.method()!=='GET'){controlRequests.push(request.method()+' '+url.pathname);return send({error:'Pen input disabled in workspace fixture'},409);}return send(penStatus);
   case '/api/admin/pen':return send({status:penStatus,targets:[penTarget]});
   case '/api/board':return send({...saved,pages:saved.pages.map(page=>({...page,buttons:page.buttons.map(button=>({...button,configured:true}))}))});
   case '/api/state':return send({pcConnected:true,obsConnected:true,scene:'Gameplay',streaming:false,recording:false,inputs:{mic:{available:true,db:-8,muted:false},desktop:{available:true,db:-14,muted:false}},session:'fixture',revision:1,execution:{session:'fixture',revision:1,active:null}});
   case '/api/execution':return send({session:'fixture',revision:1,active:null});
   case '/api/events':return route.fulfill({status:200,contentType:'text/event-stream',body:': Isolated workspace fixture.\n\n'});
   case '/api/admin/info':return send({devices:[{id:'fixture-tablet'}],addresses:['192.0.2.10'],lanAddresses:['192.0.2.10'],connectedTablets:1});
   case '/api/admin/desktop':return send({usbStatus:'Tablet connected · fixture',preferences:{}});
   case '/api/profile-lock':return send({ok:true});
   case '/api/profiles':return send({profiles:[{id:saved.profileId,name:saved.profileName}]});
   case '/api/admin/config':{
    if(request.method()==='GET')return send(saved);
    const body=request.postDataJSON();if(body.expectedRevision!==saved.boardRevision)return send({error:'Board revision changed'},409);
    if(holdSave)await new Promise(resolve=>{pendingSave=resolve;});
    saved={...saved,pages:structuredClone(body.config.pages),boardRevision:'review-save-'+(++saveCount)};
    return send({ok:true,boardRevision:saved.boardRevision});
   }
   case '/api/computer':return send({settings,activePresetId:'coding',foreground:{application:'Code.exe',available:true},contextToken:'fixture-context',status:'Ready for Code.exe',capabilities:{displayNote:'Independent display setup is optional.'}});
   case '/api/admin/computer':return send(settings);
   case '/api/computer/display':return send({available:false,active:false,sharedWithTablet:false,independentDesktop:false,driver:{installed:false,name:'Virtual display'},detail:'Install a supported virtual display driver on the PC to extend your desktop.'});
   case '/api/computer/media':return send({sessions:[]});
   case '/api/apps':return send({apps:[]});
   default:if(request.method()!=='GET'){controlRequests.push(request.method()+' '+url.pathname);return send({error:'PC actions disabled in fixture'},409);}unhandled.push(request.method()+' '+url.pathname);return send({error:'Unexpected fixture endpoint'},404);
  }
 }
 let relative;try{relative=decodeURIComponent(url.pathname).replace(/^\/+/, '')||'index.html';}catch{return route.fulfill({status:400,body:''});}
 const file=path.resolve(webroot,relative),inside=path.relative(webroot,file);
 if(!inside||path.isAbsolute(inside)||inside==='..'||inside.startsWith('..'+path.sep)||!fs.existsSync(file)||!fs.statSync(file).isFile())return route.fulfill({status:404,body:''});
 const resolved=path.relative(webroot,fs.realpathSync(file));if(path.isAbsolute(resolved)||resolved==='..'||resolved.startsWith('..'+path.sep))return route.fulfill({status:404,body:''});
 const type={'.html':'text/html','.js':'text/javascript','.css':'text/css','.ttf':'font/ttf','.svg':'image/svg+xml','.png':'image/png'}[path.extname(file)]||'application/octet-stream';
 return route.fulfill({status:200,contentType:type,body:fs.readFileSync(file)});
}
async function createPage(desktop){
 const context=await browser.newContext({viewport:{width:1280,height:800},hasTouch:!desktop});await context.route('**/*',routeFixture);
 if(!desktop)await context.addInitScript(()=>{
  window.fixtureNativeRequests=[];
  window.DeckNative={dispatch(text){
   const request=JSON.parse(text);window.fixtureNativeRequests.push(request.path);
   const result=request.path==='/native/connection'?Promise.resolve({paired:true,mode:'usb',host:'127.0.0.1'}):request.path==='/local/handwriting'?Promise.resolve({cancelled:true}):request.path==='/local/fullscreen'?Promise.resolve({enabled:request.body.enabled===true}):request.path.startsWith('/api/')?fetch(request.path,{method:request.method,headers:{'Content-Type':'application/json'},body:request.body===undefined?undefined:JSON.stringify(request.body)}).then(response=>response.json()):Promise.resolve({error:'Unexpected native fixture request'});
   result.then(value=>window.nativeResult(request.id,value)).catch(error=>window.nativeResult(request.id,{error:error.message}));
  }};
 });
 const page=await context.newPage();page.setDefaultTimeout(8000);page.on('pageerror',error=>errors.push((desktop?'desktop: ':'tablet: ')+error.message));
 await page.goto(origin+'/'+(desktop?'desktop.html#admin=fixture':'index.html'));await page.locator(desktop?'#console-board':'.key').first().waitFor();
 return page;
}
async function settle(page){await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));}
async function capture(page,name,scope,controls,minimum=24){
 await settle(page);await page.locator(scope).evaluate(node=>node.scrollTop=0);
 const geometry=await page.evaluate(selector=>{
  const host=document.querySelector(selector);return{page:document.documentElement.scrollWidth<=innerWidth+1,content:host.scrollWidth<=host.clientWidth+1,width:innerWidth,height:innerHeight};
 },scope);
 check(geometry.page&&geometry.content,name+' has no horizontal page or workspace overflow');
 if(name.startsWith('desktop-editor')&&geometry.height>=620){
  const preview=await page.evaluate(()=>{const key=document.querySelector('.editor-key').getBoundingClientRect(),label=document.querySelector('.editor-key>span').getBoundingClientRect(),save=document.querySelector('#board-editor>.save-bar').getBoundingClientRect(),body=document.querySelector('#panel-body').getBoundingClientRect();return{complete:key.top>=body.top&&key.bottom<=save.top&&label.bottom<=save.top,saveHeight:document.querySelector('#save-config').getBoundingClientRect().height};});
  check(preview.complete,name+' exposes a complete preview key and label above sticky Save');check(preview.saveHeight>=44,name+' keeps Save at least 44px high');
 }
 if(name==='desktop-ink-narrow'){
  const navigation=await page.evaluate(()=>{
   const sidebar=document.querySelector('.console-sidebar'),side=sidebar.getBoundingClientRect(),header=document.querySelector('.console-header').getBoundingClientRect();
   const visible=selector=>{const node=document.querySelector(selector),box=node.getBoundingClientRect(),hit=document.elementFromPoint(box.x+box.width/2,box.y+box.height/2);return box.width>0&&box.height>0&&box.left>=0&&box.top>=0&&box.right<=innerWidth&&box.bottom<=innerHeight&&(hit===node||node.contains(hit));};
   return{header:visible('#console-title'),selector:visible('#app-mode'),sidebar:side.top>=0&&side.bottom<=header.top+1&&sidebar.scrollHeight<=sidebar.clientHeight+1};
  });
  check(navigation.header&&navigation.selector,name+' keeps its heading and mode selector visibly reachable');check(navigation.sidebar,name+' gives the sidebar its full height above the workspace header');
 }
 const screenshot=captureOnly.size===0||captureOnly.has(name);if(screenshot)await page.screenshot({path:path.join(output,name+'.png')});captures.push({name,...geometry,screenshot});
 for(const selector of controls){
  const control=page.locator(selector);await control.scrollIntoViewIfNeeded();const box=await control.boundingBox();
  const fits=box&&box.width>=minimum&&box.height>=minimum&&box.x>=-1&&box.x+box.width<=geometry.width+1;
  check(fits,name+' keeps '+selector+' large enough and horizontally reachable');
  await control.click({trial:true});
 }
 await page.locator(scope).evaluate(node=>node.scrollTop=0);
}
async function mode(page,value,title){
 await page.locator('#app-mode').selectOption(value);await page.waitForFunction(value=>DeskWorkspace.mode===value,value);await settle(page);
 if(title)check(await page.locator('#console-title').innerText()===title,'desktop heading follows '+value+' after panel close');
 check(await page.locator('#app-mode').inputValue()===value,'workspace selector reflects '+value);
}
async function ink(page){
 await page.locator('#pen-canvas').waitFor();await page.locator('#pen-notes').fill('Next stream\n• Check microphone and camera\n• Open with the new story\n• Save the best moment');
 await page.locator('#pen-canvas').scrollIntoViewIfNeeded();const box=await page.locator('#pen-canvas').boundingBox();
 for(const points of [[[.14,.48],[.27,.29],[.43,.48],[.58,.22],[.75,.42]],[[.14,.65],[.75,.65]],[[.68,.58],[.75,.65],[.68,.72]]]){
  await page.mouse.move(box.x+box.width*points[0][0],box.y+box.height*points[0][1]);await page.mouse.down();
  for(const [x,y] of points.slice(1))await page.mouse.move(box.x+box.width*x,box.y+box.height*y,{steps:5});await page.mouse.up();
 }
 await page.waitForFunction(()=>JSON.parse(localStorage.getItem('deskwing.pen.v1')||'{}').strokes?.length===3);
 check(await page.locator('#pen-only').isChecked(),'ink ignores fingers by default');
 await page.locator('#pen-undo').click();check(await page.evaluate(()=>JSON.parse(localStorage.getItem('deskwing.pen.v1')).strokes.length)===2,'ink undo removes the last pointer stroke');
 await page.locator('#pen-redo').click();check(await page.evaluate(()=>JSON.parse(localStorage.getItem('deskwing.pen.v1')).strokes.length)===3,'ink redo restores the pointer stroke');
 await page.locator('#pen-handwriting').click();
 if(await page.evaluate(()=>native))check((await page.locator('#pen-status').innerText()).includes('cancelled')&&await page.locator('#pen-notes').inputValue().then(text=>text.includes('Next stream')),'cancelled native handwriting preserves existing notes');
 else check((await page.locator('#pen-status').innerText()).includes('type or paste'),'browser handwriting explains its native tablet fallback');
 if(await page.locator('#notice').isVisible())await page.locator('#notice').click();
 await page.locator('#pen-only').uncheck();await page.locator('#pen-only').check();await page.locator('#pen-tool').click();
 await page.locator('#pen-workspace').evaluate(node=>node.scrollTop=0);
}
async function run(){
 fs.mkdirSync(output,{recursive:true});browser=await chromium.launch({headless:true,executablePath:process.env.TABLETDECK_CHROME||'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 const desktop=await createPage(true);
 for(const [width,height,name]of [[620,480,'desktop-main-minimum'],[604,441,'desktop-main-client-minimum']]){await desktop.setViewportSize({width,height});await capture(desktop,name,'#console-overview',['#console-connect-first','#app-mode']);}
 await desktop.setViewportSize({width:1280,height:800});await desktop.locator('#console-editor').click();await desktop.locator('#page-name').waitFor();
 for(const [width,height,name] of [[1280,800,'desktop-editor'],[1280,720,'desktop-editor-720'],[900,620,'desktop-editor-small'],[390,844,'desktop-editor-narrow']]){await desktop.setViewportSize({width,height});await capture(desktop,name,'#panel-body',['#page-name','#add-button','#save-config']);}
 await desktop.setViewportSize({width:1280,height:800});await desktop.locator('#page-name').fill('Unsaved review');await desktop.locator('#app-mode').selectOption('pen');await desktop.locator('#confirm-dialog').waitFor();
 check(await desktop.locator('#page-name').inputValue()==='Unsaved review','mode switch keeps dirty draft until a decision');await desktop.locator('#confirm-dialog button[value="cancel"]').click();
 await desktop.waitForFunction(()=>!document.querySelector('#confirm-dialog').open&&DeskWorkspace.mode==='studio'&&document.querySelector('#app-mode').value==='studio'&&document.querySelector('#board-editor')?.getClientRects().length>0);
 check(await desktop.locator('#app-mode').inputValue()==='studio'&&await desktop.locator('#board-editor').isVisible(),'cancelled mode switch keeps board editor and mode');
 holdSave=true;await desktop.locator('#save-config').click();await desktop.waitForFunction(()=>document.querySelector('#save-config').disabled);
 while(!pendingSave)await new Promise(resolve=>setTimeout(resolve,10));await desktop.locator('#app-mode').selectOption('pen');await settle(desktop);
 check(await desktop.locator('#app-mode').inputValue()==='studio'&&await desktop.locator('#board-editor').isVisible(),'in-flight save blocks workspace change');
 holdSave=false;pendingSave();pendingSave=null;await desktop.locator('#save-status').filter({hasText:'Saved'}).waitFor();
 await desktop.evaluate(()=>{deckHold={};});await desktop.locator('#app-mode').selectOption('pen');await settle(desktop);
 check(await desktop.locator('#app-mode').inputValue()==='studio','active held key blocks workspace change');await desktop.evaluate(()=>{deckHold=null;});
 await mode(desktop,'pen','Ink & notes');check(await desktop.locator('#console-pen').getAttribute('aria-current')==='page','ink marks its sidebar destination');await ink(desktop);
 for(const [width,height,name] of [[1280,800,'desktop-ink'],[900,620,'desktop-ink-small'],[390,844,'desktop-ink-narrow']]){await desktop.setViewportSize({width,height});await capture(desktop,name,'#pen-workspace',['#pen-export','#pen-tool','#pen-eraser','#pen-notes']);}
 await desktop.setViewportSize({width:1280,height:800});await mode(desktop,'computer','Computer Copilot');await desktop.getByRole('button',{name:'Edit Copilot settings',exact:true}).click();await desktop.locator('#computer-enabled').waitFor();
 await mode(desktop,'display','Second display');check(await desktop.locator('#console-display').getAttribute('aria-current')==='page','display marks its sidebar destination');
 check(await desktop.locator('#computer-display').evaluate(node=>node.open),'display workspace expands independent-display status');await capture(desktop,'desktop-display','#computer-workspace',['#app-mode']);
 await mode(desktop,'pen','Ink & notes');check(await desktop.locator('#pen-notes').inputValue().then(text=>text.includes('Next stream')),'ink notes survive workspace changes');
 await desktop.locator('#console-editor').click();await desktop.locator('#page-name').waitFor();await settle(desktop);check(await desktop.locator('#console-title').innerText()==='Board editor','returning to editor restores its header after ink');
 await desktop.context().close();saved.pages[0].name='Live studio';sharedInk=emptyInk();
 const tablet=await createPage(false);
 for(const [width,height,name] of [[1280,800,'tablet-board'],[800,600,'tablet-board-small'],[668,1070,'tablet-board-portrait']]){await tablet.setViewportSize({width,height});await capture(tablet,name,'#workspace',['.key:first-child','#app-mode'],44);}
 await tablet.setViewportSize({width:1280,height:800});await mode(tablet,'pen');await ink(tablet);
 for(const [width,height,name] of [[1280,800,'tablet-ink'],[800,600,'tablet-ink-small'],[668,1070,'tablet-ink-portrait'],[390,844,'tablet-ink-narrow']]){await tablet.setViewportSize({width,height});await capture(tablet,name,'#pen-workspace',['#pen-tool','#pen-eraser','#pen-export','#pen-notes'],44);}
 await tablet.setViewportSize({width:1280,height:800});await mode(tablet,'computer');await tablet.locator('.computer-control').first().waitFor();await capture(tablet,'tablet-computer','#computer-workspace',['[data-computer-focus="control-save"]','#app-mode'],44);
 await mode(tablet,'display');check(await tablet.locator('#computer-display-status').innerText().then(text=>text.includes('virtual display driver')),'tablet display shows setup guidance without starting a display');await capture(tablet,'tablet-display','#computer-workspace',['#app-mode'],44);
 await mode(tablet,'pen');await tablet.reload();await mode(tablet,'pen');check(await tablet.locator('#pen-notes').inputValue().then(text=>text.includes('Next stream')),'ink notes survive reload');check(await tablet.evaluate(()=>JSON.parse(localStorage.getItem('deskwing.pen.v1')).strokes.length)===3,'ink pointer strokes survive reload');
 check(controlRequests.length===0,'workspace navigation and ink never dispatch PC controls or playback');check(externalRequests.length===0,'all requests remain inside the intercepted fixture');check(unhandled.length===0,'all fixture API paths are accounted for: '+unhandled.join(', '));check(errors.length===0,'all rendered workspaces have no uncaught JavaScript errors: '+errors.join('; '));
 fs.writeFileSync(path.join(output,'workspace-rendered-results.json'),JSON.stringify({checks,failures,captures,errors,unhandled,controlRequests,externalRequests,saveCount,fixtureOnly:true,narrowFallback:'At 390px navigation and drawing/notes stack; individual panes scroll vertically.',physicalPenTested:false,nativeHandwritingTested:false},null,2));
 assert.equal(failures.length,0,failures.join('\n'));console.log(checks+' workspace checks passed; '+captures.filter(item=>item.screenshot).length+' screenshots in '+output);
}
run().catch(error=>{console.error(error);process.exitCode=1;}).finally(async()=>{pendingSave?.();await browser?.close();});
