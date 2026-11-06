// Command-line/API and JavaScript logic checks. Never starts a browser or GUI.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const https=require('node:https');
const {spawn}=require('node:child_process');
const acorn=require('acorn');
const root=path.resolve(__dirname,'..');
const output=path.join(process.env.TABLETDECK_TEST_OUTPUT||path.join(root,'.test-output'),'v13-headless');fs.mkdirSync(output,{recursive:true});
const companion=process.env.TABLETDECK_COMPANION||path.join(root,'companion/bin/Release/net10.0-windows/win-x64/DeskWing-Companion.exe');
let passed=0;const check=(value,label)=>{assert.ok(value,label);passed++;console.log('PASS '+label);};
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
let server;
async function run(){
 for(const file of ['app.js','features.js','boards.js','desktop.js','studio.js','monitor.js','console.js','updates.js','hardening.js','latency.js']){acorn.parse(fs.readFileSync(path.join(root,'companion/wwwroot',file),'utf8'),{ecmaVersion:'latest'});check(true,file+' syntax');}
 const events={},timers=new Map(),requests=[],notices=[];let nextTimer=0,mode='normal',resolveBegin;
 const classes=new Set();const button={dataset:{id:'hold'},classList:{add:c=>classes.add(c),remove:c=>classes.delete(c)},setAttribute(){},setPointerCapture(){}};
 const context=vm.createContext({kinds:{},targetHints:{},pages:[{id:'main',buttons:[{id:'hold',kind:'holdHotkey'}]}],pageId:'main',preview:false,arranging:false,console,window:{addEventListener:(event,handler)=>events[event]=handler},document:{hidden:false,querySelectorAll:()=>[button],addEventListener:(event,handler)=>events[event]=handler},setInterval:callback=>{timers.set(++nextTimer,callback);return nextTimer;},clearInterval:id=>timers.delete(id),notice:text=>notices.push(text),api:async(route,body)=>{requests.push(body);if(body.operation==='begin'&&mode==='delay')return new Promise(resolve=>resolveBegin=resolve);if(body.operation==='renew'&&mode==='fail')throw new Error('Lease ended');return {lease:'lease-123'};},button});
 vm.runInContext(fs.readFileSync(path.join(root,'companion/wwwroot/hardening.js'),'utf8'),context);
 await vm.runInContext("beginDeckHold(button,'hold')",context);check(requests.at(-1).operation==='begin'&&classes.has('holding'),'press starts hold lease and marks held state');
 await [...timers.values()][0]();check(requests.at(-1).operation==='renew','held button renews lease');
 await vm.runInContext('releaseDeckHold()',context);check(requests.at(-1).operation==='release'&&timers.size===0&&!classes.has('holding'),'release ends lease and renewal');
 mode='delay';const pending=vm.runInContext("beginDeckHold(button,'hold')",context);await vm.runInContext('releaseDeckHold()',context);resolveBegin({lease:'late-lease'});await pending;check(requests.at(-1).operation==='release'&&requests.at(-1).lease==='late-lease','release before begin response still releases late lease');
 mode='normal';await vm.runInContext("beginDeckHold(button,'hold')",context);await events.blur();check(requests.at(-1).operation==='release','window blur releases held shortcut');
 await vm.runInContext("beginDeckHold(button,'hold')",context);mode='fail';await [...timers.values()][0]();check(!vm.runInContext('hasActiveHold()',context)&&timers.size===0,'failed heartbeat stops hold and clears timer');
 mode='normal';vm.runInContext('attachHoldButtons()',context);await button.onkeydown({key:' ',repeat:false,preventDefault(){}});await sleep(0);await button.onkeyup({key:' ',preventDefault(){}});check(requests.at(-1).operation==='release','keyboard press/release uses the hold lifecycle');

 const data=fs.mkdtempSync(path.join(output,'data-'));const log=fs.openSync(path.join(output,'server.log'),'w');
 server=spawn(companion,['--no-browser','--no-tray','--data-dir',data,'--http-port','18760','--https-port','18763'],{windowsHide:true,stdio:['ignore',log,log]});
 let session;
 for(let attempt=0;attempt<60;attempt++){try{session=JSON.parse(fs.readFileSync(path.join(data,'admin-session.json'),'utf8'));const response=await fetch('http://127.0.0.1:18760/api/board',{headers:{'X-Deck-Admin':session.token}});if(response.ok)break;session=null;}catch{}await sleep(100);}
 assert.ok(session,'Headless companion started');
 async function admin(route,body){const response=await fetch('http://127.0.0.1:18760'+route,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json','X-Deck-Admin':session.token},body:body===undefined?undefined:JSON.stringify(body)});return {status:response.status,data:await response.json()};}
 function tablet(route,body,token=''){return new Promise((resolve,reject)=>{const req=https.request({hostname:'127.0.0.1',port:18763,path:route,method:body===undefined?'GET':'POST',rejectUnauthorized:false,headers:{'Content-Type':'application/json',...(token?{Authorization:'Bearer '+token}:{})}},response=>{let data='';response.on('data',chunk=>data+=chunk);response.on('end',()=>resolve({status:response.statusCode,data:data?JSON.parse(data):{}}));});req.on('error',reject);req.end(body===undefined?undefined:JSON.stringify(body));});}
 check((await tablet('/api/hold',{operation:'begin',id:'x'})).status===401,'hold endpoint requires authentication');
 const pair=(await admin('/api/admin/pairing',{})).data;const paired=await tablet('/api/pair',{code:pair.code,name:'Headless regression'});check(paired.status===200,'headless TLS pairing');const token=paired.data.token;
 for(const route of ['/api/admin/desktop','/api/admin/updates','/api/admin/soundpad/remap'])check((await tablet(route,{},token)).status===403,'tablet cannot access '+route);
 const desktop=await admin('/api/admin/desktop');check(desktop.status===200&&!desktop.data.settings.usbReconnect&&!desktop.data.settings.updateNotifications,'desktop features default off in isolated data');
 check((await admin('/api/admin/desktop',{settings:{usbReconnect:false,usbSerial:'TEST-serial',updateNotifications:false}})).status===200,'desktop preferences save without changing startup registry');
 check((await admin('/api/admin/desktop',{settings:{usbReconnect:true,usbSerial:'bad";command',updateNotifications:false}})).status===400,'desktop API rejects injected USB serial');
 check((await admin('/api/admin/desktop')).data.settings.usbSerial==='TEST-serial','invalid desktop settings do not replace saved settings');
 const config=(await admin('/api/admin/config')).data;const hold={id:'test-hold',label:'Hold to talk',kind:'holdHotkey',target:'F13',value:'',icon:'mic',color:'blue',confirm:false,steps:[]};config.pages[0].buttons.push(hold);
 check((await admin('/api/admin/config',{config,replacePassword:false})).status===200,'hold button configuration saves');
 const board=(await tablet('/api/board',undefined,token)).data;check(board.pages[0].buttons.some(button=>button.id==='test-hold'&&button.kind==='holdHotkey'&&button.target===''),'tablet receives hold ID without arbitrary keyboard target');
 check((await tablet('/api/action',{id:'test-hold'},token)).status===400,'ordinary action cannot latch hold keys');
 check((await tablet('/api/hold',{operation:'begin',id:'missing'},token)).status===400,'hold begin requires existing configured hold button');
 check((await tablet('/api/hold',{operation:'renew',lease:'wrong'},token)).status===400,'unknown hold lease cannot be renewed');
 check((await tablet('/api/hold',{operation:'release',lease:'wrong'},token)).status===200,'unknown release is harmless and idempotent');
 check((await admin('/api/admin/soundpad/remap',{linkLegacy:true})).status===400,'legacy Soundpad linking requires explicit verification');
 config.pages[0].buttons.push({...hold,id:'bad-macro',kind:'macro',target:'',steps:[hold]});check((await admin('/api/admin/config',{config,replacePassword:false})).status===400,'API rejects hold shortcut inside a macro');
 await admin('/api/admin/shutdown',{});await new Promise(resolve=>server.once('exit',resolve));server=null;fs.closeSync(log);
 fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({passed,browserOpened:false,guiOpened:false,keyboardInjected:false,registryWritten:false},null,2));console.log(passed+' headless API/JavaScript checks passed.');
}
run().catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{if(server)server.kill();});
