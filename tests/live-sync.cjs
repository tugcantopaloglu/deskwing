// Isolated OBS live-catalog API and real board rendering checks. No real OBS/Soundpad or input injection.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),os=require('node:os'),crypto=require('node:crypto'),net=require('node:net');
const {once}=require('node:events'),{spawn,spawnSync}=require('node:child_process'),{WebSocketServer}=require('ws'),{chromium}=require('playwright');
const root=path.resolve(__dirname,'..'),output=path.join(root,'.test-output/live-sync');fs.mkdirSync(output,{recursive:true});
const data=fs.mkdtempSync(path.join(os.tmpdir(),'tabletdeck-live-sync-')),build=path.join(root,'.test-output/live-sync-build');
let server,mock,browser,log,passed=0,boardRequests=0;const calls=[],identifies=[],errors=[],fixtureErrors=[];
const check=(value,label)=>{assert.ok(value,label);passed++;console.log('PASS '+label);},sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const ids=[1,2,3,4].map(n=>`00000000-0000-4000-8000-${String(n).padStart(12,'0')}`);
let scenes=[{sceneUuid:ids[1],sceneName:'Chat',sceneIndex:1},{sceneUuid:ids[0],sceneName:'Gameplay',sceneIndex:0}],program=ids[0],available=true;
const liveId=id=>'live-scene:'+id;
async function port(){const listener=net.createServer();listener.listen(0,'127.0.0.1');await once(listener,'listening');const result=listener.address().port;await new Promise(resolve=>listener.close(resolve));return result;}
function emit(type='SceneListChanged'){for(const socket of mock.clients)if(socket.readyState===1)socket.send(JSON.stringify({op:5,d:{eventType:type,eventIntent:4,eventData:{}}}));}
async function run(){
 let executable=process.env.TABLETDECK_COMPANION;
 if(!executable){const compiled=spawnSync('dotnet',['build',path.join(root,'companion/DeskWing.csproj'),'--configuration','Release','--no-restore','-o',build],{cwd:root,windowsHide:true,encoding:'utf8'});assert.equal(compiled.status,0,compiled.stdout+'\n'+compiled.stderr);executable=path.join(build,'DeskWing-Companion.exe');}
 assert.ok(path.resolve(executable).includes(path.sep+'.test-output'+path.sep),'Use an isolated compiled executable in .test-output, never the running default bin.');
 const [httpPort,httpsPort,controlPort,obsPort]=await Promise.all([port(),port(),port(),port()]);assert.equal(new Set([httpPort,httpsPort,controlPort,obsPort]).size,4);
 mock=new WebSocketServer({host:'127.0.0.1',port:obsPort});await once(mock,'listening');
 mock.on('connection',socket=>{
  if(!available){socket.close(1011,'Fixture offline');return;}
  socket.send(JSON.stringify({op:0,d:{rpcVersion:1}}));
  socket.on('message',buffer=>{
   const message=JSON.parse(buffer);
   if(message.op===1){identifies.push(message.d);socket.send(JSON.stringify({op:2,d:{negotiatedRpcVersion:1}}));return;}
   const {requestType:type,requestId,requestData:args={}}=message.d;calls.push({type,args});let result={};
   if(type==='GetSceneList')result={currentProgramSceneName:scenes.find(s=>s.sceneUuid===program)?.sceneName||'',scenes};
   else if(type==='GetStreamStatus'||type==='GetRecordStatus')result={outputActive:false};
   else if(type==='GetInputVolume')result={inputVolumeDb:-12};
   else if(type==='GetInputMute')result={inputMuted:false};
   else if(type==='GetInputList')result={inputs:[{inputName:'Mic/Aux'},{inputName:'Desktop Audio'}]};
   else if(type==='SetCurrentProgramScene'){const match=scenes.find(s=>args.sceneUuid?s.sceneUuid===args.sceneUuid:s.sceneName===args.sceneName);if(match)program=match.sceneUuid;}
   else {fixtureErrors.push(type);socket.send(JSON.stringify({op:7,d:{requestType:type,requestId,requestStatus:{result:false,code:207,comment:'Unexpected fixture OBS call '+type},responseData:{}}}));return;}
   socket.send(JSON.stringify({op:7,d:{requestType:type,requestId,requestStatus:{result:true,code:100},responseData:result}}));
  });
 });
 log=fs.openSync(path.join(output,'server.log'),'w');
 server=spawn(executable,['--no-browser','--no-tray','--data-dir',data,'--http-port',String(httpPort),'--https-port',String(httpsPort),'--control-port',String(controlPort)],{cwd:root,windowsHide:true,stdio:['ignore',log,log]});
 let session;const base='http://127.0.0.1:'+httpPort;
 for(let attempt=0;attempt<100;attempt++){try{session=JSON.parse(fs.readFileSync(path.join(data,'admin-session.json'),'utf8'));if((await fetch(base+'/api/board',{headers:{'X-Deck-Admin':session.token}})).ok)break;session=null;}catch{}if(server.exitCode!==null)throw new Error('Isolated server exited: '+fs.readFileSync(path.join(output,'server.log'),'utf8'));await sleep(100);}
 assert.ok(session,'isolated companion started');
 async function request(route,body){const response=await fetch(base+route,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json','X-Deck-Admin':session.token},body:body===undefined?undefined:JSON.stringify(body)});const text=await response.text();return{status:response.status,data:text?JSON.parse(text):{}};}
 async function waitBoard(predicate){const until=Date.now()+6500;let value;do{value=(await request('/api/board')).data;if(predicate(value))return value;await sleep(100);}while(Date.now()<until);return value;}
 const cfg=(await request('/api/admin/config')).data;cfg.obsPort=obsPort;
 const saved={id:'saved-scenes-manual',label:'Saved manual scene',kind:'scene',target:'Legacy scene',value:'',icon:'clapper',color:'violet',confirm:false,steps:[]};
 const manual={...saved,id:'separate-manual',label:'Separate manual scene',target:'Gameplay'};
 const scenePage={id:crypto.randomUUID().replaceAll('-',''),name:'Scenes',icon:'clapper',liveScenes:false,buttons:[saved]};
 cfg.pages=[scenePage,{id:crypto.randomUUID().replaceAll('-',''),name:'Manual',icon:'grid',liveScenes:false,buttons:[manual]}];
 check((await request('/api/admin/config',{config:cfg})).status===200,'fixture config saves without touching user profile');
 const savedManual=JSON.stringify((await request('/api/admin/config')).data.pages[0].buttons);
 check((await request('/api/admin/import-scenes',{})).status===200,'Import OBS scenes enables live projection');
 let board=(await request('/api/board')).data,live=board.pages[0];
 check(live.liveScenes&&JSON.stringify(live.buttons.map(b=>b.label))===JSON.stringify(['Gameplay','Chat']),'live page follows sceneIndex rather than response array order');
 check(live.buttons[0].id===liveId(ids[0])&&live.buttons[0].target==='Gameplay','live buttons expose stable UUID identity and current scene target');
 check(JSON.stringify((await request('/api/admin/config')).data.pages[0].buttons)===savedManual,'import preserves saved manual buttons under live projection');
 check(board.pages[1].buttons[0].id===manual.id&&!board.pages[1].liveScenes,'separate manual page remains unchanged');
 check(identifies.some(i=>(i.eventSubscriptions&4)===4),'OBS connection subscribes to scene events');
 const previousRevision=board.sceneCatalogRevision;
 scenes=[{sceneUuid:ids[0],sceneName:'Gameplay renamed',sceneIndex:2},{sceneUuid:ids[1],sceneName:'Chat',sceneIndex:0},{sceneUuid:ids[2],sceneName:'New scene',sceneIndex:1}];emit('SceneNameChanged');await sleep(80);
 board=(await request('/api/board')).data;
 check(board.sceneCatalogRevision!==previousRevision,'catalog revision changes on rename/create/reorder event');
 check(JSON.stringify(board.pages[0].buttons.map(b=>b.label))===JSON.stringify(['Chat','New scene','Gameplay renamed']),'renamed/new/reordered scenes appear without importing again');
 check(board.pages[0].buttons[2].id===liveId(ids[0]),'rename retains original scene UUID button identity');
 check((await request('/api/action',{id:liveId(ids[0])})).status===200,'stale displayed UUID resolves a current scene at action time');
 const switchCall=calls.filter(c=>c.type==='SetCurrentProgramScene').at(-1);
 check(switchCall.args.sceneUuid===ids[0]&&!Object.hasOwn(switchCall.args,'sceneName'),'live dispatch switches by UUID after rename');
 let mutationCount=calls.filter(c=>c.type==='SetCurrentProgramScene').length;
 check((await request('/api/action',{id:'live-scene:../../anything'})).status===400,'malformed runtime identity is rejected');
 check((await request('/api/action',{id:liveId(ids[3])})).status===409,'invented well-formed scene UUID is rejected by current catalog');
 scenes=scenes.filter(s=>s.sceneUuid!==ids[0]);emit('SceneRemoved');await sleep(80);
 check((await request('/api/action',{id:liveId(ids[0])})).status===409,'deleted scene UUID is rejected even if old button remains on a client');
 check(calls.filter(c=>c.type==='SetCurrentProgramScene').length===mutationCount,'invalid/invented/deleted scene actions never switch another scene');
 let edit=(await request('/api/admin/config')).data;
 const invalid=structuredClone(edit);invalid.pages[1].buttons.push({...manual,id:'invalid-runtime-kind',kind:'liveScene',target:liveId(ids[1])});
 check((await request('/api/admin/config',{config:invalid})).status===400,'internal runtime action kind cannot be persisted as a configured action');
 edit.pages[0].liveScenes=false;check((await request('/api/admin/config',{config:edit})).status===200,'live sync can be disabled in PC config');
 board=(await request('/api/board')).data;check(!board.pages[0].liveScenes&&board.pages[0].buttons[0].id===saved.id,'disabling live sync restores saved manual buttons');
 check((await request('/api/action',{id:liveId(ids[1])})).status===400,'runtime scene identities are unavailable when all live pages are disabled');
 edit.pages[0].liveScenes=true;await request('/api/admin/config',{config:edit});

 // Serve the real tablet HTML under a fixture URL to avoid the host's desktop-console root redirect.
 browser=await chromium.launch({executablePath:process.env.TABLETDECK_CHROME||'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});
 const page=await browser.newPage({viewport:{width:1280,height:800}});page.on('pageerror',error=>errors.push(error.message));
 await page.addInitScript(()=>sessionStorage.setItem('deckSetupSeen','1'));
 await page.route('**/fixture-board',route=>route.fulfill({contentType:'text/html',body:fs.readFileSync(path.join(root,'companion/wwwroot/index.html'),'utf8')}));
 await page.route('**/api/soundpad/**',route=>route.fulfill({status:409,json:{error:'Soundpad is excluded from the OBS live-sync suite.'}}));
 await page.route('https://**',route=>route.abort());
 page.on('request',r=>{if(new URL(r.url()).pathname==='/api/board')boardRequests++;});
 await page.goto(base+'/fixture-board#admin='+session.token);await page.locator('#keys .key').first().waitFor();
 check((await page.locator('#keys .key-label').allTextContents()).join('|')==='Chat|New scene','actual tablet board initially renders current live scene collection');
 await page.waitForFunction(()=>hasLiveState());check(true,'actual board establishes authenticated live state stream');
 await sleep(150);const requestsBefore=boardRequests;
 scenes=[{sceneUuid:ids[2],sceneName:'New scene renamed',sceneIndex:0},{sceneUuid:ids[1],sceneName:'Chat',sceneIndex:1}];emit('SceneNameChanged');
 try{await page.waitForFunction(()=>document.querySelector('#keys .key-label')?.textContent==='New scene renamed',{},{timeout:6000});}
 catch(error){console.error('Live board diagnostic',await page.evaluate(()=>({state,liveCatalogRevision,refreshing,lastBoard,panelOpen:panel.open,hidden:document.hidden,pages})),{boardRequests,errors,obsRequests:calls.slice(-12)});throw error;}
 check(boardRequests>requestsBefore&&(await page.locator('#keys .key-label').allTextContents()).join('|')==='New scene renamed|Chat','OBS event pushes a catalog revision and refreshes visible board without manual refresh');
 const beforeActive=boardRequests;program=ids[1];emit('CurrentProgramSceneChanged');
 await page.waitForFunction(()=>document.querySelector('.key.active')?.dataset.id==='live-scene:00000000-0000-4000-8000-000000000002',{},{timeout:6000});
 check(true,'program-scene event updates active marker on current UUID button');
 check(boardRequests===beforeActive,'program-scene changes do not need a catalog board reload');
 const readonlyBefore=calls.filter(c=>!c.type.startsWith('Get')).length;
 scenes=[];emit('SceneListChanged');
 await page.locator('#keys .empty-key').waitFor({timeout:6000});
 check((await page.locator('#keys .empty-key').innerText()).includes('No live OBS scenes available'),'empty scene collection renders honest empty state');
 check(calls.filter(c=>!c.type.startsWith('Get')).length===readonlyBefore,'automatic catalog and active-state sync issue read-only OBS requests');
 available=false;for(const socket of mock.clients)socket.close(1011,'Fixture disconnect');
 board=await waitBoard(b=>b.sceneCatalogRevision==='offline');check(board.sceneCatalogRevision==='offline'&&board.pages[0].buttons.length===0,'disconnected OBS produces empty live page and offline catalog revision');
 available=true;scenes=[{sceneUuid:ids[1],sceneName:'Recovered collection',sceneIndex:0}];
 board=await waitBoard(b=>b.pages[0].buttons[0]?.label==='Recovered collection');check(board.pages[0].buttons[0]?.label==='Recovered collection','live scene collection recovers after OBS reconnect');
 check(errors.length===0,'actual board push lifecycle has no JavaScript errors: '+errors.join(', '));
 check(fixtureErrors.length===0,'all OBS requests stay inside the explicit mock protocol: '+fixtureErrors.join(', '));
 check(!calls.some(c=>c.type.startsWith('Start')||c.type.startsWith('Toggle')),'suite never starts media/streaming/recording or injects keys');
 await browser.close();browser=null;
 const exit=once(server,'exit');await request('/api/admin/shutdown',{});await exit;server=null;
 for(const socket of mock.clients)socket.terminate();await new Promise(resolve=>mock.close(resolve));mock=null;fs.closeSync(log);log=undefined;
 fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({passed,errors,fixtureErrors,boardRequests,obsFixture:true,soundpadUsed:false,keyboardInjected:false,visibleGuiOpened:false,isolatedExecutable:executable},null,2));
 console.log(passed+' isolated live OBS API and headless board checks passed.');
}
run().catch(error=>{console.error(error);process.exitCode=1;}).finally(async()=>{if(browser)await browser.close();if(server)server.kill();if(mock){for(const socket of mock.clients)socket.terminate();await new Promise(resolve=>mock.close(resolve));}if(log!==undefined)fs.closeSync(log);});
