'use strict';

// Isolated companion and private test data. No desktop input or real tablet controls run.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const https=require('node:https');
const {randomBytes,X509Certificate}=require('node:crypto');
const {spawn,spawnSync}=require('node:child_process');

const root=path.resolve(__dirname,'..');
const exe=process.env.TABLETDECK_COMPANION||path.join(root,'.test-output/ink-integration-build/Release/net10.0-windows10.0.19041.0/win-x64/TabletDeck-Companion.exe');
const output=fs.mkdtempSync(path.join(os.tmpdir(),'deskwing-ink-api-'));
const data=path.join(output,'data');
const base='http://127.0.0.1:19740';
fs.mkdirSync(data);
let server,session,log,passed=0;
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const check=(condition,label)=>{assert.ok(condition,label);passed++;console.log('PASS '+label);};
const document=(notes='')=>({version:1,strokes:[],notes,penOnly:true,updatedAt:'2026-11-05T10:00:00.000Z'});
const stroke=()=>({tool:'pen',color:'#295a95',width:3,points:[[0,0,0.05],[0.25,0.5,0.7],[1,1,1]]});

async function request(route,{body,raw,auth='admin',headers={}}={}){
  const response=await fetch(base+route,{
    method:body===undefined&&raw===undefined?'GET':'POST',
    headers:{'Content-Type':'application/json',...(auth==='admin'?{'X-Deck-Admin':session.token}:auth==='none'?{}:{Authorization:'Bearer '+auth}),...headers},
    body:raw===undefined?(body===undefined?undefined:JSON.stringify(body)):raw,
    signal:AbortSignal.timeout(15000)
  });
  const text=await response.text();let value;
  try{value=text?JSON.parse(text):{};}catch{value={};}
  return{status:response.status,data:value,text,headers:response.headers};
}

function fixtureTls(){
  // Trust only the certificate created inside this test's private data directory.
  const script='$ErrorActionPreference="Stop"; $certificate=[System.Security.Cryptography.X509Certificates.X509Certificate2]::new([IO.File]::ReadAllBytes($env:DESKWING_INK_TEST_PFX),$null,[System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet); try { [Console]::Write([Convert]::ToBase64String($certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))) } finally { $certificate.Dispose() }';
  const exported=spawnSync('powershell.exe',['-NoProfile','-NonInteractive','-Command',script],{windowsHide:true,encoding:'utf8',timeout:15000,env:{...process.env,DESKWING_INK_TEST_PFX:path.join(data,'companion.pfx')}});
  if(exported.error||exported.status!==0)throw Error('Could not read the isolated companion certificate: '+(exported.error?.message||exported.stderr));
  const certificate=new X509Certificate(Buffer.from(exported.stdout.trim(),'base64'));
  return {ca:certificate.toString(),checkServerIdentity:(_host,peer)=>peer.raw?.equals(certificate.raw)?undefined:Error('The test companion certificate did not match its private fixture.')};
}

function oversizedUpload(chunked,token,tlsOptions){
  // Observe an early HTTP rejection without fetch discarding it when an unread upload is reset.
  return new Promise((resolve,reject)=>{
    let remaining=4*1024*1024+1,settled=false;
    const upload=https.request('https://127.0.0.1:19743/api/ink',{...tlsOptions,method:'POST',headers:{'Content-Type':'application/json',Authorization:'Bearer '+token,...(chunked?{}:{'Content-Length':remaining,Expect:'100-continue'})}},response=>{
      settled=true;response.resume();response.once('end',()=>upload.destroy());resolve(response.statusCode);
    });
    upload.on('error',error=>{if(!settled)reject(error);});
    upload.setTimeout(15000,()=>upload.destroy(Error('Oversized fixture upload timed out')));
    const send=()=>{
      if(settled||upload.destroyed)return;
      if(remaining===0){upload.end();return;}
      const count=Math.min(remaining,65536);remaining-=count;upload.write(Buffer.alloc(count,32),send);
    };
    if(chunked)send();else{upload.once('continue',send);upload.flushHeaders();}
  });
}

async function start(){
  assert.ok(fs.statSync(exe,{throwIfNoEntry:false})?.isFile(),'an isolated companion build is required');
  log=fs.openSync(path.join(output,'server-'+Date.now()+'.log'),'w');
  session=null;
  server=spawn(exe,['--no-browser','--no-tray','--data-dir',data,'--http-port','19740','--https-port','19743','--control-port','19744'],{windowsHide:true,stdio:['ignore',log,log]});
  let failure;server.once('error',error=>{failure=error;});
  for(let i=0;i<120;i++){
    if(failure)throw failure;
    if(server.exitCode!==null)throw Error('Isolated ink companion exited before readiness; see '+output);
    try{
      session=JSON.parse(fs.readFileSync(path.join(data,'admin-session.json'),'utf8'));
      if((await request('/api/admin/config')).status===200)return;
    }catch{}
    session=null;await sleep(100);
  }
  throw Error('Isolated ink companion did not become ready; see '+output);
}

async function stop(){
  if(!server)return;
  const processToStop=server;
  if(!processToStop.pid){server=null;session=null;if(log!==undefined){fs.closeSync(log);log=undefined;}return;}
  let timer;
  const exited=processToStop.exitCode!==null?Promise.resolve():new Promise(resolve=>processToStop.once('exit',resolve));
  try{
    if(session&&processToStop.exitCode===null)await request('/api/admin/shutdown',{body:{}});
    await Promise.race([exited,new Promise((_,reject)=>{timer=setTimeout(()=>reject(Error('Test companion shutdown timed out')),10000);})]);
  }catch{
    if(processToStop.exitCode===null){processToStop.kill();await exited;}
  }finally{
    clearTimeout(timer);server=null;session=null;
    if(log!==undefined){fs.closeSync(log);log=undefined;}
  }
}

async function run(){
  const secret=randomBytes(32).toString('hex');
  const commandPath=path.join(output,'must-not-run.exe');
  fs.writeFileSync(path.join(data,'config.json'),JSON.stringify({profileId:'b'.repeat(32),profileName:'Ink fixture',obsPort:19745,obsPassword:secret,pages:[{id:'page',name:'Main',buttons:[{id:'never',label:'Never run',kind:'app',target:commandPath}]}]}));
  await start();
  const tlsOptions=fixtureTls();

  check((await request('/api/ink',{auth:'none'})).status===401,'ink read requires a paired tablet or local administrator');
  check((await request('/api/ink',{auth:'none',body:{expectedRevision:'A'.repeat(64),document:document()}})).status===401,'ink write requires authentication before document handling');
  check((await request('/api/ink',{headers:{Origin:'https://untrusted.invalid'}})).status===403,'cross-origin ink read is rejected despite an administrator credential');
  check((await request('/api/ink',{body:{},headers:{'Sec-Fetch-Site':'cross-site'}})).status===403,'cross-site ink write is rejected');
  check((await request('/api/ink',{raw:'{}',headers:{'Content-Type':'text/plain'}})).status===415,'ink accepts only JSON request bodies');
  check((await request('/api/ink',{raw:'{invalid'})).status===400,'malformed ink JSON returns a readable validation failure');

  const initial=(await request('/api/ink')).data;
  check(/^[a-f0-9]{32}$/i.test(initial.workspaceId)&&/^[a-f0-9]{64}$/i.test(initial.revision)&&initial.document.version===1&&initial.document.strokes.length===0&&initial.document.notes===''&&initial.recoveryWarning==='','new PC exposes an empty ink document and stable identities');
  const pin=(await request('/api/admin/pairing',{body:{}})).data;
  const pair=(await request('/api/pair',{auth:'none',body:{code:pin.code,name:'Ink API fixture'}})).data;
  assert.ok(pair.token,'test tablet pairs');
  check((await request('/api/ink',{auth:pair.token})).data.revision===initial.revision,'paired tablet sees the same document as the PC');

  const first={...document('Tablet handwriting <b>stays text</b>'),strokes:[stroke()]};
  const saved=await request('/api/ink',{auth:pair.token,body:{expectedRevision:initial.revision,document:first}});
  check(saved.status===200&&saved.data.workspaceId===initial.workspaceId&&saved.data.revision!==initial.revision,'paired tablet can save a shared pressure-sensitive stroke');
  assert.deepEqual(saved.data.document,first);
  const visible=(await request('/api/ink')).data;
  check(visible.revision===saved.data.revision&&JSON.stringify(visible.document)===JSON.stringify(first),'tablet ink and notes are immediately visible to the PC');
  const unchanged=await request('/api/ink?revision='+saved.data.revision,{auth:pair.token});
  check(unchanged.status===200&&unchanged.data.unchanged===true&&unchanged.data.workspaceId===initial.workspaceId&&unchanged.data.revision===saved.data.revision&&typeof unchanged.data.recoveryWarning==='string'&&!('document' in unchanged.data),'unchanged polling returns identities without retransmitting the ink document');
  const changedPoll=await request('/api/ink?revision='+initial.revision,{auth:pair.token});
  check(changedPoll.status===200&&changedPoll.data.revision===saved.data.revision&&JSON.stringify(changedPoll.data.document)===JSON.stringify(first),'polling from an older revision returns the full current ink document');
  for(const [label,query] of [['invalid revision','revision=invalid'],['unknown query key','revision='+saved.data.revision+'&extra=1'],['duplicate revision','revision='+saved.data.revision+'&revision='+initial.revision]])check((await request('/api/ink?'+query)).status===400,'polling rejects '+label);
  check(!JSON.stringify(visible).includes(secret)&&!JSON.stringify(visible).includes(commandPath)&&!JSON.stringify(visible).includes(pair.token),'ink snapshots contain no PC credentials, pairing tokens, or executable targets');
  const conflict=await request('/api/ink',{body:{expectedRevision:initial.revision,document:document('stale overwrite')}});
  check(conflict.status===409&&conflict.data.code==='ink_conflict'&&conflict.data.revision===saved.data.revision&&conflict.data.workspaceId===initial.workspaceId,'stale save returns a reviewable conflict with current revision');
  check((await request('/api/ink')).data.document.notes===first.notes,'stale save leaves existing shared ink unchanged');

  const invalid=[
    ['missing expected revision',{document:document()}],
    ['unknown document command',{expectedRevision:saved.data.revision,document:{...document(),command:'run'}}],
    ['unsupported schema version',{expectedRevision:saved.data.revision,document:{...document(),version:2}}],
    ['out-of-range point',{expectedRevision:saved.data.revision,document:{...document(),strokes:[{...stroke(),points:[[1.01,0,0.5]]}]}}],
    ['unsafe color',{expectedRevision:saved.data.revision,document:{...document(),strokes:[{...stroke(),color:'url(file:///private)'}]}}],
    ['overlong notes',{expectedRevision:saved.data.revision,document:document('x'.repeat(8001))}]
  ];
  for(const [label,body] of invalid)check((await request('/api/ink',{body})).status===400,'rejects '+label);
  const duplicate='{"expectedRevision":'+JSON.stringify(saved.data.revision)+',"expectedRevision":'+JSON.stringify(initial.revision)+',"document":'+JSON.stringify(document())+'}';
  check((await request('/api/ink',{raw:duplicate})).status===400,'duplicate request keys are rejected instead of choosing an ambiguous revision');
  const nonfinite=JSON.stringify({expectedRevision:saved.data.revision,document:{...document(),strokes:[stroke()]}}).replace('[0,0,0.05]','[1e400,0,0.05]');
  check((await request('/api/ink',{raw:nonfinite})).status===400,'non-finite numeric coordinates are rejected');
  check((await request('/api/ink')).data.revision===saved.data.revision,'all rejected documents preserve the last good revision');

  const concurrent=await Promise.all(['PC winner','Tablet winner'].map(notes=>request('/api/ink',{body:{expectedRevision:saved.data.revision,document:document(notes)}})));
  check(concurrent.filter(result=>result.status===200).length===1&&concurrent.filter(result=>result.status===409).length===1,'concurrent saves from one revision have exactly one winner');
  let current=(await request('/api/ink')).data;
  check(current.document.notes===concurrent.find(result=>result.status===200).data.document.notes,'atomic save preserves the winning document');

  const large={...document('Large legitimate drawing'),strokes:[{...stroke(),points:Array.from({length:60000},()=>[0.123456,0.654321,0.555])}]};
  const largeBody={expectedRevision:current.revision,document:large};
  assert.ok(Buffer.byteLength(JSON.stringify(largeBody))>1024*1024,'large fixture exceeds default request cap');
  const largeSaved=await request('/api/ink',{auth:pair.token,body:largeBody});
  check(largeSaved.status===200&&largeSaved.data.document.strokes[0].points.length===60000,'ink route accepts a valid drawing larger than the default 1 MiB body limit');
  current=largeSaved.data;
  check(await oversizedUpload(false,pair.token,tlsOptions)===413,'ink request body is capped at 4 MiB over pinned HTTPS');
  check(await oversizedUpload(true,pair.token,tlsOptions)===413,'streamed ink without Content-Length is also capped at 4 MiB over pinned HTTPS');
  const unrelated=await request('/api/profile-lock',{body:{active:false,padding:'x'.repeat(1024*1024)}});
  check(unrelated.status===413||[400,409].includes(unrelated.status)&&/too large|request body|maximum.*size/i.test(unrelated.text),'the ink exception does not enlarge another endpoint body limit');
  check((await request('/api/ink')).data.revision===current.revision,'oversized requests leave shared ink intact');

  const backup=(await request('/api/admin/backups',{body:{}})).data;
  const changed=await request('/api/ink',{body:{expectedRevision:current.revision,document:document('Ink stays after board restore')}});
  assert.equal(changed.status,200);current=changed.data;
  const before=(await request('/api/admin/config')).data;
  const edit=await request('/api/admin/config',{body:{config:{...before,profileName:'Edited board'},expectedRevision:before.boardRevision}});
  assert.equal(edit.status,200);
  check((await request('/api/admin/backups/restore',{body:{id:backup.id,confirmed:true}})).status===200,'fixture board backup restores successfully');
  const afterRestore=(await request('/api/ink')).data;
  check(afterRestore.revision===current.revision&&afterRestore.document.notes==='Ink stays after board restore','board backup restore preserves separate shared ink');
  check(fs.readdirSync(path.join(data,'backups')).filter(name=>name.endsWith('.json')).every(name=>!fs.readFileSync(path.join(data,'backups',name),'utf8').includes('Ink stays after board restore')),'board backup files do not silently capture the separate ink document');

  await stop();await start();
  const restarted=await request('/api/ink',{auth:pair.token});
  check(restarted.status===200&&restarted.data.workspaceId===current.workspaceId&&restarted.data.revision===current.revision&&restarted.data.document.notes===current.document.notes,'shared ink identity, revision, content, and tablet access survive restart');
  console.log(passed+' ink API checks passed; real desktop input and tablet controls unused.');
}

run().catch(error=>{console.error(error);process.exitCode=1;}).finally(stop);
