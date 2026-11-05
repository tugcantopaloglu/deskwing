// Isolated editor contracts. No playback, keyboard injection, device control or live data.
const assert=require('node:assert/strict'),fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),crypto=require('node:crypto'),{spawn}=require('node:child_process');
const root=path.resolve(__dirname,'..'),fixture=fs.mkdtempSync(path.join(os.tmpdir(),'tabletdeck-reliability-api-'));
const executable=process.env.TABLETDECK_COMPANION||path.join(root,'build/backend-review/bin/TabletDeck-Companion.exe');
let child,session,passed=0;const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const check=(condition,label)=>{assert.ok(condition,label);passed++;console.log('PASS '+label);};
async function port(){const server=net.createServer();await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const value=server.address().port;await new Promise(resolve=>server.close(resolve));return value;}
async function run(){
 const fixturePassword=crypto.randomBytes(32).toString('hex');
 const [httpPort,httpsPort,controlPort]=await Promise.all([port(),port(),port()]);
 const log=fs.openSync(path.join(fixture,'service.log'),'w');
 child=spawn(executable,['--no-browser','--no-tray','--data-dir',fixture,'--http-port',String(httpPort),'--https-port',String(httpsPort),'--control-port',String(controlPort)],{windowsHide:true,stdio:['ignore',log,log]});
 const api=async(route,body,headers={})=>{const response=await fetch(`http://127.0.0.1:${httpPort}${route}`,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json','X-Deck-Admin':session?.token||'',...headers},body:body===undefined?undefined:JSON.stringify(body)});const text=await response.text();return{status:response.status,data:text?JSON.parse(text):null};};
 try{
  for(let i=0;i<100;i++){try{session=JSON.parse(fs.readFileSync(path.join(fixture,'admin-session.json'),'utf8'));if((await api('/api/admin/config')).status===200)break;}catch{}await sleep(100);}
  assert.ok(session,'isolated companion starts');
  const first=(await api('/api/admin/config')).data;check(/^[A-F0-9]{64}$/.test(first.boardRevision),'editor receives an opaque board revision');
  const edit=structuredClone(first);edit.pages[0].name='Updated once';
  const saved=await api('/api/admin/config',{config:edit,expectedRevision:first.boardRevision});
  check(saved.status===200&&saved.data.boardRevision!==first.boardRevision,'revision-aware save returns new revision');
  const stale=await api('/api/admin/config',{config:first,expectedRevision:first.boardRevision});
  check(stale.status===409&&/changed/.test(stale.data.error)&&(await api('/api/admin/config')).data.pages[0].name==='Updated once','stale HTTP save returns recoverable conflict and keeps newer content');
  const fresh=(await api('/api/admin/config')).data;
  check((await api('/api/admin/obs-settings',{port:19999,micInput:'Latest mic',desktopInput:'Latest desktop',passwordMode:'replace',password:fixturePassword})).status===200,'dedicated OBS settings route updates setup');
  fresh.pages[0].name='Edited after setup';check((await api('/api/admin/config',{config:fresh,expectedRevision:fresh.boardRevision})).status===200,'independent setup change does not conflict with board edit');
  const current=(await api('/api/admin/config')).data;check(current.obsPort===19999&&current.micInput==='Latest mic'&&current.passwordConfigured&&current.obsPassword==='','board save preserves newer setup and redacts password');
  check(JSON.parse(fs.readFileSync(path.join(fixture,'config.json'),'utf8')).obsPassword===fixturePassword,'board save retains the exact generated OBS credential privately');
  check((await api('/api/admin/import-scenes',{profileId:current.profileId,expectedRevision:first.boardRevision})).status===409,'scene import rejects a stale board revision before contacting OBS');
  check((await api('/api/admin/import-scenes',{profileId:crypto.randomUUID().replaceAll('-',''),expectedRevision:current.boardRevision})).status===409,'scene import rejects a different profile before contacting OBS');
  const targetId=(await api('/api/admin/profiles',{operation:'create',name:'Destination',duplicate:false})).data.id;
  const target=(await api(`/api/admin/profiles/${targetId}/config`)).data;
  check(target.profileId===targetId&&target.pages.length===1&&typeof target.boardRevision==='string'&&!('obsPassword'in target),'inactive profile endpoint exposes board fields without credentials');
  const transfer={sourceProfileId:current.profileId,sourceRevision:current.boardRevision,targetProfileId:targetId,targetRevision:target.boardRevision,sourcePageId:current.pages[0].id,targetPageId:target.pages[0].id,buttonIds:[current.pages[0].buttons[0].id],operation:'copy'};
  const copied=await api('/api/admin/profiles/transfer',transfer);
  check(copied.status===200&&copied.data.sourceRevision===current.boardRevision&&copied.data.targetRevision!==target.boardRevision,'transfer atomically returns both resulting revisions');
  check((await api('/api/admin/profiles/transfer',transfer)).status===409,'stale target transfer is rejected over HTTP');
  check((await api('/api/admin/config')).data.profileId===current.profileId,'cross-profile copy leaves tablet active board unchanged');
  check((await api(`/api/admin/profiles/${targetId}/config`,undefined,{'X-Deck-Admin':''})).status===403,'inactive target contents require local admin');
  check((await api('/api/admin/profiles/transfer',transfer,{'X-Deck-Admin':''})).status===403,'profile transfer requires local admin');
  check((await api('/api/admin/profiles/transfer',transfer,{Origin:'https://external.invalid'})).status===403,'cross-origin profile transfer is refused');
  console.log(passed+' reliability API checks passed; no live controls used.');
 }finally{
  if(child&&child.exitCode===null){try{await api('/api/admin/shutdown',{});}catch{}await Promise.race([new Promise(resolve=>child.once('exit',resolve)),sleep(3000)]);if(child.exitCode===null)child.kill();}
  fs.closeSync(log);
 }
}
run().catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{if(child&&child.exitCode===null)child.kill();});
