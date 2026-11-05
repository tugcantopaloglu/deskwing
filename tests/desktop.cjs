// Isolated PC setup API and controller checks. No browser, registry writes, real OBS, or audio.
const assert=require('node:assert/strict'),fs=require('node:fs'),os=require('node:os'),path=require('node:path'),vm=require('node:vm'),acorn=require('acorn'),{spawn}=require('node:child_process');
const root=path.resolve(__dirname,'..'),output=path.join(process.env.TABLETDECK_TEST_OUTPUT||os.tmpdir(),'tabletdeck-desktop-tests');fs.mkdirSync(output,{recursive:true});
const executable=process.env.TABLETDECK_COMPANION||path.join(root,'companion/bin/Release/net10.0-windows/win-x64/TabletDeck-Companion.exe');
let server,passed=0;const check=(value,label)=>{assert.ok(value,label);passed++;console.log('PASS '+label);},sleep=ms=>new Promise(r=>setTimeout(r,ms));
async function apiTests(){
 const data=fs.mkdtempSync(path.join(output,'data-')),log=fs.openSync(path.join(output,'server.log'),'w');
 server=spawn(executable,['--no-browser','--no-tray','--data-dir',data,'--http-port','19080','--https-port','19083'],{windowsHide:true,stdio:['ignore',log,log]});
 let session;for(let i=0;i<80;i++){try{session=JSON.parse(fs.readFileSync(path.join(data,'admin-session.json'),'utf8'));if((await fetch('http://127.0.0.1:19080/api/board',{headers:{'X-Deck-Admin':session.token}})).ok)break;session=null;}catch{}await sleep(100);}assert.ok(session,'isolated setup companion starts');
 async function request(route,body,admin=true,headers={}){const response=await fetch('http://127.0.0.1:19080'+route,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json',...(admin?{'X-Deck-Admin':session.token}:{}),...headers},body:body===undefined?undefined:JSON.stringify(body)});const text=await response.text();return{status:response.status,data:text?JSON.parse(text):{}};}
 const settings={port:4455,micInput:'Mic/Aux',desktopInput:'Desktop Audio',passwordMode:'replace',password:'fixture-secret-not-a-credential'};
 check((await request('/api/admin/obs-settings',settings,false)).status===403,'OBS setup requires local admin authentication');
 const pin=(await request('/api/admin/pairing',{})).data,pair=(await request('/api/pair',{code:pin.code,name:'Setup fixture'},false)).data;
 check((await request('/api/admin/obs-settings',settings,false,{Authorization:'Bearer '+pair.token})).status===403,'paired tablet cannot change OBS settings');
 check((await request('/api/admin/obs-settings',settings,true,{Origin:'https://untrusted.invalid'})).status===403,'cross-origin setup writes are refused');
 const original=(await request('/api/admin/config')).data;
 await request('/api/admin/profiles',{operation:'create',name:'Other board',duplicate:true});
 const before=JSON.parse(fs.readFileSync(path.join(data,'config.json'),'utf8'));
 check((await request('/api/admin/obs-settings',settings)).data.ok,'dedicated OBS settings save succeeds');
 let disk=JSON.parse(fs.readFileSync(path.join(data,'config.json'),'utf8'));
 check(disk.obsPassword===settings.password,'replacement OBS password is stored privately');
 check(JSON.stringify(disk.pages)===JSON.stringify(before.pages)&&JSON.stringify(disk.profiles)===JSON.stringify(before.profiles)&&JSON.stringify(disk.favourites)===JSON.stringify(before.favourites),'OBS setup preserves active pages, inactive profiles and favourites');
 check((await request('/api/admin/config')).data.obsPassword===''&&(await request('/api/admin/config')).data.passwordConfigured,'OBS password is redacted from setup reads');
 check((await request('/api/admin/obs-settings',{...settings,passwordMode:'keep',password:'',micInput:'  New mic  '})).data.ok,'blank password keep mode saves independently');
 disk=JSON.parse(fs.readFileSync(path.join(data,'config.json'),'utf8'));
 check(disk.obsPassword===settings.password&&disk.micInput==='New mic','keep preserves saved password and trims input names');
 for(const invalid of [{port:0},{port:65536},{port:1.1},{passwordMode:'guess'},{micInput:null},{desktopInput:'x'.repeat(201)},{password:'x'.repeat(513)}])check((await request('/api/admin/obs-settings',{...settings,...invalid})).status===400,'invalid OBS field is refused: '+Object.keys(invalid)[0]);
 check(JSON.parse(fs.readFileSync(path.join(data,'config.json'),'utf8')).obsPassword===settings.password,'invalid requests cannot replace saved settings');
 const board=(await request('/api/admin/config')).data;board.pages[0].buttons.push({id:'setup-wait',label:'Setup wait',kind:'macro',steps:[{label:'Wait',kind:'delay',target:'10000'}]});await request('/api/admin/config',{config:board});
 const run=request('/api/action',{id:'setup-wait'});let active;
 for(let i=0;i<80;i++){active=(await request('/api/execution')).data.active;if(active)break;await sleep(20);}assert.ok(active);
 check((await request('/api/admin/obs-settings',settings)).status===409,'OBS settings cannot change during a running macro');
 await request('/api/cancel',{id:active.id});await run;
 check((await request('/api/admin/obs-settings',{...settings,passwordMode:'clear',password:''})).data.ok&&!JSON.parse(fs.readFileSync(path.join(data,'config.json'),'utf8')).obsPassword,'explicit clear removes only the saved OBS password');
 const backups=(await request('/api/admin/backups')).data.backups;
 check(backups.length>0&&fs.readdirSync(path.join(data,'backups')).filter(f=>f.endsWith('.json')).every(f=>!fs.readFileSync(path.join(data,'backups',f),'utf8').includes(settings.password)),'OBS settings edits create password-free recovery snapshots');
 check((await request('/api/board',undefined,false,{Authorization:'Bearer '+pair.token})).status===200,'setup changes preserve tablet pairing');
 check((await request('/api/admin/config')).data.profileId===original.profileId,'setup changes retain the active profile identity');
 await request('/api/admin/shutdown',{});await new Promise(resolve=>server.once('exit',resolve));server=null;fs.closeSync(log);
}
function fixture(){
 const nodes=new Map(),requests=[],notices=[],calls=[],storage=new Map();
 const values={devices:[],settings:{},usbStatus:'Automatic USB reconnect is off.',profiles:[{active:true,name:'<custom board>',pages:1,buttons:3}],backups:[],limit:20,obsConnected:false,inputs:{},obsPort:4455,micInput:'Mic',desktopInput:'Desktop',passwordConfigured:true};
 const $=id=>{if(!nodes.has(id))nodes.set(id,{value:id==='#setup-obs-port'?'4455':id==='#setup-mic'?'Mic':id==='#setup-desktop'?'Desktop':'',checked:false,disabled:false,textContent:'',innerHTML:'',hidden:true,querySelectorAll:()=>[...nodes.values()]});return nodes.get(id);};
 const context=vm.createContext({$,native:false,preview:false,dirty:false,arranging:false,editing:false,featureView:'',panelGeneration:0,Date,DOMPurify:{sanitize:html=>html},document:{body:{classList:{add:c=>calls.push(c)}},addEventListener(){}},sessionStorage:{getItem:key=>storage.get(key),setItem:(key,value)=>storage.set(key,value)},notice:(...args)=>notices.push(args),esc:value=>String(value).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),panel:{open:false,close(){this.open=false;}},openPanel:(title,html)=>{context.html=html;context.panelGeneration++;context.panel.open=true;nodes.clear();for(const match of html.matchAll(/id="([^"]+)"/g))$('#'+match[1]);},api:async(route,body)=>{requests.push({route,body});return values;},confirmAction:async()=>true});
 for(const name of ['showPairing','showEditor','showProfiles','showBackups','showSoundpadLibrary','showDesktopSettings'])context[name]=()=>calls.push(name);
 vm.runInContext(fs.readFileSync(path.join(root,'companion/wwwroot/desktop.js'),'utf8'),context);return{context,$,nodes,requests,notices,calls,storage,values};
}
async function uiTests(){
 let f=fixture(),c=f.context;
 await c.showDesktopHub();check(c.html.includes('&lt;custom board&gt;')&&!c.html.includes('<custom board>'),'PC setup escapes custom profile text');
 check(c.html.includes('Pair your first tablet')&&c.html.includes('Not connected')&&c.html.includes('No snapshots yet'),'first-run hub explains disconnected and empty states');
 await f.$('#setup-edit').onclick();await f.$('#setup-backups').onclick();check(f.calls.includes('showEditor')&&f.calls.includes('showBackups'),'visible setup actions open editor and backups');
 check(f.requests.every(r=>r.body===undefined)&&!f.requests.some(r=>/pairing|updates|soundpad|\/action/.test(r.route)),'status checks do not run actions, query Soundpad, generate PINs or contact update service');
 f.values.devices=[{name:'Tablet'}];await c.showDesktopHub();check(c.html.includes('1 paired tablet')&&c.html.includes('does not mean a tablet is currently connected'),'paired count is not represented as online status');
 c.dirty=true;const count=f.requests.length;await c.showDesktopHub();check(f.requests.length===count&&f.notices.length>0,'unsaved edits block setup navigation');c.dirty=false;c.arranging=true;await c.showDesktopHub();check(f.requests.length===count,'button arrangement blocks setup navigation');
 f=fixture();c=f.context;let resolves=[];c.api=()=>new Promise(resolve=>resolves.push(resolve));const late=c.showDesktopHub();c.openPanel('Editor','Unsaved editor');c.featureView='editor';c.dirty=true;resolves.forEach(resolve=>resolve(f.values));await late;check(c.html==='Unsaved editor'&&c.dirty,'late hub checks cannot overwrite newer editor contents');
 f=fixture();c=f.context;resolves=[];c.api=()=>new Promise(resolve=>resolves.push(resolve));const closed=c.showDesktopHub();c.panel.close();resolves.forEach(resolve=>resolve(f.values));await closed;check(!c.panel.open,'late checks cannot reopen a dismissed panel');
 f=fixture();c=f.context;c.api=async()=>{throw Error('PC session expired');};await c.showDesktopHub();check(c.html.includes('Retry checks')&&f.$('#desktop-feedback').textContent.includes('PC session expired'),'failed checks show a visible error with retry and unlock guidance');
 f=fixture();c=f.context;await c.initializeDesktopSetup();check(c.featureView==='pc-setup'&&f.calls.includes('pc-editor'),'first unpaired PC opens setup and enables PC-specific layout');
 const bootCount=f.requests.length;await c.initializeDesktopSetup();check(f.requests.length===bootCount,'first-run setup appears once per browser tab session');
 f=fixture();c=f.context;c.native=true;await c.initializeDesktopSetup();check(f.requests.length===0&&f.$('#pc-setup').hidden,'Android does not initialize PC setup');
 f=fixture();c=f.context;c.preview=true;await c.initializeDesktopSetup();check(f.requests.length===0,'design preview does not initialize PC setup');
 f=fixture();c=f.context;let resolve;c.api=()=>new Promise(r=>resolve=r);const boot=c.initializeDesktopSetup();c.openPanel('Editor','Keep typing');c.dirty=true;resolve(f.values);await boot;check(c.html==='Keep typing'&&!f.storage.has('deckSetupSeen'),'delayed first-run detection cannot interrupt existing edits');
 f=fixture();c=f.context;await c.showObsSetup();f.$('#obs-setup-form').oninput();check(c.dirty,'OBS form edits participate in unsaved-changes protection');
 const event={preventDefault(){}};await f.$('#obs-setup-form').onsubmit(event);const saved=f.requests.find(r=>r.route==='/api/admin/obs-settings');check(saved.body.passwordMode==='keep'&&saved.body.password===''&&!('pages'in saved.body),'OBS form sends only connection fields and keeps a blank password');
 check(!c.dirty&&f.$('#desktop-feedback').textContent==='OBS settings saved.','successful save clears dirty state and reports confirmation');
 f.$('#setup-obs-password').value='replacement';await f.$('#obs-setup-form').onsubmit(event);check(f.requests.filter(r=>r.route==='/api/admin/obs-settings').at(-1).body.passwordMode==='replace'&&f.$('#setup-obs-password').value==='','replacement password is sent explicitly and cleared from the field');
 f.$('#setup-clear-password').checked=true;f.$('#setup-obs-password').value='conflict';const saves=f.requests.length;await f.$('#obs-setup-form').onsubmit(event);check(f.requests.length===saves&&f.$('#desktop-feedback').textContent.includes('either'),'conflicting password choices are rejected visibly');
 f.$('#setup-obs-password').value='';c.confirmAction=async()=>false;await f.$('#obs-setup-form').onsubmit(event);check(f.requests.length===saves,'declining password clearing sends no write');
 f.$('#setup-clear-password').checked=false;f.$('#setup-obs-port').value='65536';await f.$('#obs-setup-form').onsubmit(event);check(f.requests.length===saves,'invalid port fails before network write');
 f=fixture();c=f.context;await c.showObsSetup();c.api=()=>new Promise(r=>resolve=r);const pending=f.$('#obs-setup-form').onsubmit(event);check(f.$('#setup-mic').disabled&&f.$('#setup-obs-save').disabled,'pending save locks fields against edits that a response could lose');const oldRequests=f.requests.length;await f.$('#obs-setup-form').onsubmit(event);check(f.requests.length===oldRequests,'pending save does not start another submission');
 c.openPanel('Editor','New view');c.featureView='editor';c.dirty=true;resolve({ok:true});await pending;check(c.html==='New view'&&c.dirty,'late OBS save cannot clear dirty state or touch a newer panel');
 f=fixture();c=f.context;c.api=()=>new Promise(r=>resolve=r);const load=c.showObsSetup();c.panel.close();resolve(f.values);await load;check(!c.panel.open,'late OBS form load cannot reopen a dismissed panel');
 function legacy(f){f.context.window=f.context;f.context.addEventListener=()=>{};vm.runInContext(fs.readFileSync(path.join(root,'companion/wwwroot/editor.js'),'utf8'),f.context);for(const [file,names] of [['app.js',['showEditor','showPairing']],['hardening.js',['showDesktopSettings']]]){const source=fs.readFileSync(path.join(root,'companion/wwwroot',file),'utf8'),ast=acorn.parse(source,{ecmaVersion:'latest'});for(const node of ast.body.filter(n=>n.type==='FunctionDeclaration'&&names.includes(n.id.name)))vm.runInContext(source.slice(node.start,node.end),f.context);}f.context.renderEditor=()=>f.context.openPanel('Editor','Loaded board');}
 for(const name of ['showEditor','showPairing','showDesktopSettings']){
  f=fixture();c=f.context;legacy(f);c.api=()=>new Promise(r=>resolve=r);const pending=c[name]();
  check(c.panel.open&&c.panelGeneration===1,name+' owns a loading panel before awaiting');
  c.openPanel('OBS','Newer OBS edits');c.featureView='obs-setup';c.dirty=true;resolve(f.values);await pending;
  check(c.html==='Newer OBS edits'&&c.dirty,name+' cannot replace a newer unsaved OBS form');
  f=fixture();c=f.context;legacy(f);c.api=()=>new Promise(r=>resolve=r);const closed=c[name]();c.panel.close();resolve(f.values);await closed;
  check(!c.panel.open,name+' cannot reopen a dismissed panel');
 }
 f=fixture();c=f.context;legacy(f);f.values.update={url:'https://github.com/tugcantopaloglu/tablet-deck-studio/releases',currentVersion:'1.8.0'};
 await c.showDesktopSettings();f.$('#desktop-serial').oninput();check(c.dirty,'Windows option edits join unsaved-changes protection');
 c.api=()=>new Promise(r=>resolve=r);const windowsSave=f.$('#desktop-save').onclick();check(f.$('#desktop-serial').disabled,'Windows settings lock fields while saving');
 c.openPanel('Editor','Newer unsaved board');c.featureView='editor';c.dirty=true;resolve({ok:true});await windowsSave;check(c.html==='Newer unsaved board'&&c.dirty,'late Windows save cannot clear dirty state in another view');
}
(async()=>{await apiTests();await uiTests();fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({passed,browserOpened:false,guiOpened:false,realObsUsed:false,registryWritten:false},null,2));console.log(passed+' desktop API/UI controller checks passed.');})().catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{if(server)server.kill();});
