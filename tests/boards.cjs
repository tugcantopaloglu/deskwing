// Real isolated HTTPS API and UI controller logic; no browser or streaming app.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),https=require('node:https'),vm=require('node:vm'),acorn=require('acorn'),{spawn}=require('node:child_process');
const root=path.resolve(__dirname,'..'),folder=path.join(process.env.TABLETDECK_TEST_OUTPUT||path.join(root,'.test-output'),'boards');fs.mkdirSync(folder,{recursive:true});
const executable=process.env.TABLETDECK_COMPANION||path.join(root,'companion/bin/Release/net10.0-windows/win-x64/TabletDeck-Companion.exe');
let server,passed=0;const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms)),check=(value,label)=>{assert.ok(value,label);passed++;console.log('PASS '+label);};
async function run(){
 const data=fs.mkdtempSync(path.join(folder,'data-')),key='A'.repeat(64),secret='fixture-original-password';
 fs.writeFileSync(path.join(data,'config.json'),JSON.stringify({obsPassword:secret,pages:[{id:'original-page',name:'Main',icon:'home',buttons:[{id:'wait',label:'Wait macro',kind:'macro',steps:[{label:'Wait',kind:'delay',target:'10000'},{label:'Never',kind:'delay',target:'10000'}]},{id:'tap',label:'Tap',kind:'delay',target:'0'}]}],favourites:[{key,name:'Fixture favourite'}]}));
 let session,log;
 async function start(){log=fs.openSync(path.join(folder,'server-'+Date.now()+'.log'),'w');server=spawn(executable,['--no-browser','--no-tray','--data-dir',data,'--http-port','19060','--https-port','19063'],{windowsHide:true,stdio:['ignore',log,log]});for(let i=0;i<80;i++){try{session=JSON.parse(fs.readFileSync(path.join(data,'admin-session.json'),'utf8'));if((await fetch('http://127.0.0.1:19060/api/board',{headers:{'X-Deck-Admin':session.token}})).ok)return;}catch{}await sleep(100);}throw Error('Isolated board companion failed to start');}
 async function admin(route,body){const response=await fetch('http://127.0.0.1:19060'+route,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json','X-Deck-Admin':session.token},body:body===undefined?undefined:JSON.stringify(body)});return{status:response.status,data:await response.json()};}
 function tablet(route,body,token=''){return new Promise((resolve,reject)=>{const request=https.request({host:'127.0.0.1',port:19063,path:route,method:body===undefined?'GET':'POST',rejectUnauthorized:false,headers:{'Content-Type':'application/json',...(token?{Authorization:'Bearer '+token}:{})}},response=>{let text='';response.on('data',chunk=>text+=chunk);response.on('end',()=>{try{resolve({status:response.statusCode,data:text?JSON.parse(text):{}});}catch(error){reject(error);}});});request.on('error',reject);request.end(body===undefined?undefined:JSON.stringify(body));});}
 async function stop(){await admin('/api/admin/shutdown',{});await new Promise(resolve=>server.once('exit',resolve));server=null;fs.closeSync(log);}
 await start();
 check((await tablet('/api/profiles')).status===401&&(await tablet('/api/profile',{id:'x'})).status===401&&(await tablet('/api/soundpad/favourites')).status===401,'profile and favourite routes require authentication');
 const pin=(await admin('/api/admin/pairing',{})).data,pair=(await tablet('/api/pair',{code:pin.code,name:'Boards fixture'})).data,token=pair.token;
 for(const route of ['/api/admin/profiles','/api/admin/backups','/api/admin/backups/preview','/api/admin/backups/restore'])check((await tablet(route,{},token)).status===403,'paired tablet cannot manage '+route);
 const original=(await admin('/api/admin/config')).data,initialId=original.profileId;
 check(initialId.length===32&&original.passwordConfigured&&original.obsPassword===''&&!('profiles'in original)&&!('favourites'in original),'editor sees only active board with redacted password');
 const summaries=(await tablet('/api/profiles',undefined,token)).data;check(summaries.profiles[0].id===initialId&&!JSON.stringify(summaries).includes('target')&&!JSON.stringify(summaries).includes(secret),'remote profiles expose summaries without PC targets or secrets');
 const copy=(await admin('/api/admin/profiles',{operation:'create',name:'Gaming <custom>',duplicate:true})).data.id;
 check(typeof copy==='string'&&(await tablet('/api/profiles',undefined,token)).data.profiles.length===2,'local profile duplication persists and is visible remotely');
 check((await admin('/api/admin/profiles',{operation:'create',name:' '})).status===400,'invalid profile name is rejected at API boundary');
 check((await admin('/api/admin/profiles',{operation:'command'})).status===400,'unknown management operation is rejected');
 check((await tablet('/api/profile',{id:copy},token)).data.ok,'tablet can switch to a configured profile');
 const copiedBoard=(await tablet('/api/board',undefined,token)).data,wait=copiedBoard.pages[0].buttons.find(b=>b.kind==='macro');
 check(copiedBoard.profileId===copy&&copiedBoard.profileName==='Gaming <custom>'&&!copiedBoard.pages[0].buttons.some(b=>b.id==='tap'),'switch returns current name and independently regenerated button IDs');
 check((await tablet('/api/action',{id:'tap'},token)).status===400,'stale tablet action cannot trigger copied-profile controls');
 check((await admin('/api/admin/config',{config:original})).status===409,'stale editor cannot overwrite a newly selected profile');
 check((await admin('/api/admin/profiles',{operation:'delete',id:initialId})).status===400,'profile deletion requires explicit confirmation');
 check((await admin('/api/admin/profiles',{operation:'delete',id:copy,confirmed:true})).status===400,'active profile cannot be deleted');
 const pending=tablet('/api/action',{id:wait.id},token);let active;
 for(let i=0;i<80;i++){active=(await tablet('/api/execution',undefined,token)).data.active;if(active?.step===1)break;await sleep(20);}assert.ok(active);
 check((await tablet('/api/profile',{id:initialId},token)).status===409,'profile cannot switch while a macro is executing');
 check((await admin('/api/admin/profiles',{operation:'rename',id:copy,name:'Busy'})).status===409,'management cannot edit boards while a macro is executing');
 await tablet('/api/cancel',{id:active.id},token);await pending;
 const backup=(await admin('/api/admin/backups',{})).data,preview=(await admin('/api/admin/backups/preview',{id:backup.id})).data;
 check(preview.credentialsPreserved&&preview.profiles.length===2&&preview.backup.favourites===1&&!JSON.stringify(preview).includes(secret)&&!JSON.stringify(preview).includes('obsPassword'),'restore preview summarizes all profiles and favourites without credentials');
 check((await tablet('/api/soundpad/favourites',undefined,token)).data.favourites[0].key===key,'saved favourite metadata is available without running Soundpad');
 check((await tablet('/api/soundpad/favourites',{key:'../config.json',selected:true},token)).status===400,'invalid favourite identity fails before contacting Soundpad');
 check((await tablet('/api/soundpad/favourites',{key,selected:false},token)).data.ok&&(await tablet('/api/soundpad/favourites',undefined,token)).data.favourites.length===0,'missing-library favourite can be removed without Soundpad');
 const count=(await admin('/api/admin/backups')).data.backups.length;await tablet('/api/soundpad/favourites',{key,selected:false},token);check((await admin('/api/admin/backups')).data.backups.length===count,'no-op favourite removal does not consume backup history');
 await admin('/api/admin/profiles',{operation:'rename',id:copy,name:'Changed'});await admin('/api/admin/profiles',{operation:'create',name:'Extra'});
 const edit=(await admin('/api/admin/config')).data;edit.obsPassword='fixture-current-password';await admin('/api/admin/config',{config:edit,replacePassword:true});
 check((await admin('/api/admin/backups/restore',{id:backup.id})).status===400,'restore requires explicit confirmation');
 check((await admin('/api/admin/backups/preview',{id:'../../config'})).status===400,'backup endpoint rejects arbitrary paths');
 check((await admin('/api/admin/backups/restore',{id:backup.id,confirmed:true})).data.ok,'confirmed restore succeeds');
 const restored=(await tablet('/api/board',undefined,token)).data,persisted=JSON.parse(fs.readFileSync(path.join(data,'config.json'),'utf8'));
 check(restored.profileName==='Gaming <custom>'&&(await tablet('/api/profiles',undefined,token)).data.profiles.length===2&&(await tablet('/api/soundpad/favourites',undefined,token)).data.favourites.length===1,'restore recovers the previewed profile collection and favourites');
 check(persisted.obsPassword==='fixture-current-password'&&(await tablet('/api/board',undefined,token)).status===200,'restore preserves current password and existing tablet credential');
 check(fs.readdirSync(path.join(data,'backups')).filter(name=>name.endsWith('.json')).every(name=>!fs.readFileSync(path.join(data,'backups',name),'utf8').includes('fixture-current-password')),'automatic backup files do not contain current credentials');
 await stop();await start();check((await tablet('/api/board',undefined,token)).data.profileId===copy&&(await tablet('/api/soundpad/favourites',undefined,token)).data.favourites.length===1,'profiles favourites and tablet pairing survive restart');

 const source=fs.readFileSync(path.join(root,'companion/wwwroot/features.js'),'utf8'),ast=acorn.parse(source,{ecmaVersion:'latest'}),fn=ast.body.find(n=>n.type==='FunctionDeclaration'&&n.id.name==='favouriteView');
 const view=vm.runInNewContext(source.slice(fn.start,fn.end)+';favouriteView');
 const favourite=[{key,name:'Old name'}],moved=[{key,index:57,name:'New name',categories:[]}];
 check(view(moved,favourite,true)[0].index===57&&view(moved,favourite,true)[0].name==='New name','favourite UI follows stable identity to current index and title');
 check(view([],favourite,true)[0].unavailable&&view([...moved,{...moved[0],index:58}],favourite,true)[0].unavailable,'missing and ambiguous favourites render unavailable instead of choosing a sound');
 check(view(moved,favourite,false)===moved&&view(moved,[],true).length===0,'all-sounds view stays intact and empty favourites have an empty view');
 const nodes=new Map(),requests=[],buttons=[{dataset:{useProfile:'other'},disabled:false}],notices=[];function $(id){if(!nodes.has(id))nodes.set(id,{textContent:'',value:id==='#manage-profile'?'active':'',disabled:false});return nodes.get(id);}
 const context=vm.createContext({$,dirty:false,arranging:false,busy:false,executionStatus:null,hasActiveHold:()=>false,notice:(...args)=>notices.push(args),native:false,preview:false,featureView:'',editing:false,panelGeneration:0,lastBoard:1,esc:text=>String(text).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),openPanel:(title,html)=>{context.html=html;context.panelGeneration++;context.panel.open=true;},document:{querySelectorAll:selector=>selector==='[data-use-profile]'?buttons:[]},panel:{open:true,close(){context.panel.open=false;}},refresh:async()=>{},api:async(route,body)=>{requests.push({route,body});return route==='/api/profiles'?{profiles:[{id:'active',name:'<img src=x>',pages:1,buttons:2,active:true},{id:'other',name:'Other',pages:1,buttons:0,active:false}]}:{ok:true};},confirmAction:async()=>true});
 vm.runInContext(fs.readFileSync(path.join(root,'companion/wwwroot/boards.js'),'utf8'),context);
 await context.showProfiles();check(context.html.includes('&lt;img src=x&gt;')&&!context.html.includes('<img src=x>'),'profile names are escaped in the management panel');
 await buttons[0].onclick();check(requests.some(r=>r.route==='/api/profile'&&r.body.id==='other'),'profile switch control sends configured profile identity');
 context.dirty=true;check(!context.canChangeBoard(),'unsaved edits block profile switch');context.dirty=false;context.arranging=true;check(!context.canChangeBoard(),'arrangement blocks profile switch');context.arranging=false;context.busy=true;check(!context.canChangeBoard(),'pending action blocks profile switch');context.busy=false;context.executionStatus={active:{}};check(!context.canChangeBoard(),'remote running action blocks profile switch');context.executionStatus=null;context.hasActiveHold=()=>true;check(!context.canChangeBoard(),'held keys block profile switch');
 check($('#board-status').textContent.includes('release held keys'),'profile validation feedback is visible inside the settings panel');
 context.hasActiveHold=()=>false;context.dirty=false;
 const profileReply={profiles:[{id:'active',name:'Fresh profile',pages:1,buttons:2,active:true}]};let finish;
 context.api=async()=>new Promise(resolve=>finish=resolve);
 const lateProfiles=context.showProfiles();context.openPanel('Edit','New editor contents');context.featureView='editor';context.editing=true;context.dirty=true;finish(profileReply);await lateProfiles;
 check(context.html==='New editor contents'&&context.dirty&&context.editing,'late Profiles response cannot replace a newer unsaved editor');
 context.dirty=false;const closedProfiles=context.showProfiles();context.panel.close();finish(profileReply);await closedProfiles;check(!context.panel.open,'late Profiles response cannot reopen a closed panel');
 const lateBackups=context.showBackups();context.openPanel('Another view','Other contents');context.featureView='apps';finish({backups:[],limit:20});await lateBackups;check(context.html==='Other contents'&&context.featureView==='apps','late Backups response cannot replace a different panel');
 context.api=route=>route==='/api/admin/backups/compare'?Promise.resolve({profiles:[],favouritesAdded:0,favouritesRemoved:0,settingsChanged:[]}):new Promise(resolve=>finish=resolve);
 const latePreview=context.previewBackup('fixture');context.openPanel('Another view','Preview must stay away');context.featureView='apps';finish({backup:{created:new Date().toISOString(),reason:'Fixture',favourites:0},profiles:[],current:[]});await latePreview;check(context.html==='Preview must stay away','late backup preview cannot replace a newer panel');
 context.api=async(route,body)=>route==='/api/profiles'?profileReply:new Promise(resolve=>finish=resolve);
 await context.showProfiles();$('#new-profile-name').value='Later';const lateSave=$('#profile-empty').onclick();context.panel.close();finish({ok:true});await lateSave;check(!context.panel.open,'successful management request does not reopen Profiles after dismissal');

 const focusNodes=ast.body.filter(n=>n.type==='FunctionDeclaration'&&['toggleSoundpadFavourite','captureLibraryFocus','restoreLibraryFocus'].includes(n.id.name));
 function focusFixture(saved=[],rows=true,delayed=false){
  const body={},search={},old={dataset:{favouriteKey:key},disabled:false,isConnected:true},replacement={dataset:{favouriteKey:key},focus(){focus.document.activeElement=replacement;}},filter={focus(){focus.document.activeElement=filter;}};let release;let renders=0;
  const others=['favouriteKey','playSound','selectSound'].map(property=>({dataset:{[property]:property==='favouriteKey'?'B'.repeat(64):'57'}}));
  const nextOthers=others.map(other=>({dataset:other.dataset,focus(){focus.document.activeElement=this;}}));const oldNodes=new Set([old,...others]);const results={contains:node=>oldNodes.has(node)};
  const focus=vm.createContext({panelGeneration:1,featureView:'library',panel:{open:true},libraryFavourites:saved,librarySounds:[{index:3,key},{index:57,key:'B'.repeat(64)}],document:{body,activeElement:old,querySelectorAll:selector=>!rows?[]:selector==='[data-favourite-key]'?[replacement,nextOthers[0]]:selector==='[data-play-sound]'?[nextOthers[1]]:[nextOthers[2]]},$:selector=>selector==='#library-results'?results:filter,api:async(route,payload)=>{if(payload&&delayed)await new Promise(resolve=>release=resolve);return{favourites:saved.length?[]:[{key,name:'Sound'}]};},renderSoundpadResults:()=>{renders++;old.isConnected=false;if(oldNodes.has(focus.document.activeElement))focus.document.activeElement=body;}});
  for(const node of focusNodes)vm.runInContext(source.slice(node.start,node.end),focus);return{focus,old,replacement,filter,search,others,nextOthers,get renders(){return renders;},release:()=>release({ok:true})};
 }
 let focus=focusFixture();await focus.focus.toggleSoundpadFavourite(focus.old);check(focus.focus.document.activeElement===focus.replacement,'starring a sound restores keyboard focus to its stable-key replacement');
 focus=focusFixture([{key,name:'Sound'}],false);await focus.focus.toggleSoundpadFavourite(focus.old);check(focus.focus.document.activeElement===focus.filter,'removing the focused favourite row moves focus to the filter');
 focus=focusFixture([],true,true);const movedFocus=focus.focus.toggleSoundpadFavourite(focus.old);focus.focus.document.activeElement=focus.search;focus.release();await movedFocus;check(focus.focus.document.activeElement===focus.search,'late favourite response does not steal focus after the user moved it');
 focus=focusFixture([],true,true);const staleFavourite=focus.focus.toggleSoundpadFavourite(focus.old);focus.focus.featureView='apps';focus.focus.panelGeneration++;focus.release();await staleFavourite;check(focus.renders===0,'late favourite response cannot rerender a different settings view');
 for(const [index,type] of ['star','Play','checkbox'].entries()){focus=focusFixture([],true,true);const pendingFocus=focus.focus.toggleSoundpadFavourite(focus.old);focus.focus.document.activeElement=focus.others[index];focus.release();await pendingFocus;check(focus.focus.document.activeElement===focus.nextOthers[index],'focus moved to another result '+type+' survives the pending favourite update');}
 await stop();fs.writeFileSync(path.join(folder,'results.json'),JSON.stringify({passed,browserOpened:false,guiOpened:false,realSoundpadUsed:false},null,2));console.log(passed+' boards API/UI-logic checks passed.');
}
run().catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{if(server)server.kill();});
