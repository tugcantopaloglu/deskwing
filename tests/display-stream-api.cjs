'use strict';

// HTTP boundaries only: this fixture never creates/shares a display or captures pixels.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const net=require('node:net');
const https=require('node:https');
const crypto=require('node:crypto');
const {spawn,spawnSync}=require('node:child_process');

const root=path.resolve(__dirname,'..');
const fixture=fs.mkdtempSync(path.join(os.tmpdir(),'deskwing-display-stream-api-'));
const executable=process.env.TABLETDECK_COMPANION||path.join(root,'build/backend-review/bin/TabletDeck-Companion.exe');
const unownedLease=crypto.randomBytes(24).toString('hex').toUpperCase();
const endpoints=[
  ['/api/computer/display/stream/start',{refreshRate:60},409],
  ['/api/computer/display/stream',{lease:unownedLease},409],
  ['/api/computer/display/stream/stop',{lease:unownedLease},200]
];
let child,session,log,passed=0;
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
function check(condition,label){assert.ok(condition,label);passed++;console.log('PASS '+label);}
async function port(){
  const server=net.createServer();
  await new Promise((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
  const value=server.address().port;
  await new Promise((resolve,reject)=>server.close(error=>error?reject(error):resolve()));
  return value;
}
function fixtureTls(){
  const script='$ErrorActionPreference="Stop"; $certificate=[System.Security.Cryptography.X509Certificates.X509Certificate2]::new([IO.File]::ReadAllBytes($env:DESKWING_DISPLAY_STREAM_TEST_PFX),$null,[System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet); try { [Console]::Write([Convert]::ToBase64String($certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))) } finally { $certificate.Dispose() }';
  const result=spawnSync('powershell.exe',['-NoProfile','-NonInteractive','-Command',script],{
    windowsHide:true,encoding:'utf8',timeout:60000,
    env:{...process.env,DESKWING_DISPLAY_STREAM_TEST_PFX:path.join(fixture,'companion.pfx')}
  });
  if(result.error||result.status!==0)throw Error('Could not read the isolated test certificate: '+(result.error?.message||result.stderr));
  const certificate=new crypto.X509Certificate(Buffer.from(result.stdout.trim(),'base64'));
  return {
    ca:certificate.toString(),
    checkServerIdentity:(_host,peer)=>peer.raw?.equals(certificate.raw)?undefined:Error('The test companion certificate does not match its private fixture.')
  };
}
async function run(){
  const chosen=new Set();
  for(let attempt=0;chosen.size<3&&attempt<100;attempt++)chosen.add(await port());
  assert.equal(chosen.size,3,'three distinct isolated ports are available');
  const [httpPort,httpsPort,controlPort]=[...chosen];
  log=fs.openSync(path.join(fixture,'service.log'),'w');
  child=spawn(executable,['--no-browser','--no-tray','--data-dir',fixture,'--http-port',String(httpPort),'--https-port',String(httpsPort),'--control-port',String(controlPort)],{
    windowsHide:true,stdio:['ignore',log,log]
  });
  let launchError;
  child.once('error',error=>{launchError=error;});
  async function api(route,{body,raw,auth='admin',headers={},timeout=15000}={}){
    const credentials=auth==='admin'?{'X-Deck-Admin':session?.token||''}:auth==='none'?{}:{Authorization:'Bearer '+auth};
    const response=await fetch(`http://127.0.0.1:${httpPort}${route}`,{
      method:body===undefined&&raw===undefined?'GET':'POST',
      headers:{'Content-Type':'application/json',...credentials,...headers},
      body:raw===undefined?(body===undefined?undefined:JSON.stringify(body)):raw,
      signal:AbortSignal.timeout(timeout)
    });
    const text=await response.text();let data={};
    try{data=text?JSON.parse(text):{};}catch{}
    return {status:response.status,data};
  }
  try{
    let ready=false;
    const deadline=Date.now()+30000;
    while(Date.now()<deadline){
      if(launchError)throw launchError;
      if(child.exitCode!==null)throw Error('Isolated companion exited before readiness. See '+path.join(fixture,'service.log'));
      try{
        session=JSON.parse(fs.readFileSync(path.join(fixture,'admin-session.json'),'utf8'));
        if((await api('/api/admin/config',{timeout:1000})).status===200){ready=true;break;}
      }catch{}
      await sleep(100);
    }
    assert.ok(ready,'isolated companion becomes ready before any stream request');
    const tls=fixtureTls();
    async function chunked(route,body,token){
      return new Promise((resolve,reject)=>{
        const request=https.request({
          host:'127.0.0.1',port:httpsPort,path:route,...tls,method:'POST',
          headers:{Authorization:'Bearer '+token,'Content-Type':'application/json','Transfer-Encoding':'chunked'}
        },response=>{
          response.on('error',reject);response.resume();response.on('end',()=>resolve(response.statusCode));
        });
        request.on('error',reject);
        request.setTimeout(15000,()=>request.destroy(Error('The chunked display-stream fixture timed out.')));
        request.write(body.slice(0,2048));request.end(body.slice(2048));
      });
    }

    for(const [route,body] of endpoints){
      check((await api(route,{body,auth:'none'})).status===401,route+' requires authentication');
    }
    const pin=(await api('/api/admin/pairing',{body:{}})).data;
    const name='Display stream fixture '+crypto.randomUUID();
    const paired=await api('/api/pair',{body:{code:pin.code,name},auth:'none'});
    assert.equal(paired.status,200,'private fixture tablet pairs');
    const token=paired.data.token;
    assert.match(token,/^[A-F0-9]{64}$/,'fixture uses a freshly generated device credential');

    const status=await api('/api/computer/display',{auth:token});
    check(status.status===200&&!status.data.active&&!status.data.sharedWithTablet,'read-only display status confirms no active or shared fixture display');
    check(!JSON.stringify(status.data).includes(token),'read-only display status does not expose the device credential');
    check((await api('/api/admin/computer/display',{auth:token,body:{operation:'create',width:1280,height:800,shareWithTablet:true,acceptInstalledDriver:true}})).status===403,'tablet cannot create a display or consent to its driver');
    check((await api('/api/admin/computer/display',{auth:token,body:{operation:'share',enabled:true}})).status===403,'tablet cannot enable PC display sharing');
    check((await api('/api/admin/computer/display',{body:{operation:'share',enabled:true},headers:{Origin:'https://external.invalid'}})).status===403,'cross-origin requests cannot enable sharing with an administrator credential');

    for(const [route,body,expected] of endpoints){
      check((await api(route,{body,auth:token})).status===expected,
        expected===200?route+' accepts an idempotent stop without a lease':route+' refuses a stream without a locally created and shared display');
      check((await api(route,{body,auth:token,headers:{Origin:'https://external.invalid'}})).status===403,route+' rejects cross-origin access');
      check((await api(route,{body,auth:token,headers:{'Sec-Fetch-Site':'cross-site'}})).status===403,route+' rejects cross-site access');
      const oversized=JSON.stringify({...body,padding:'x'.repeat(4096)});
      assert.ok(Buffer.byteLength(oversized)>4096,'oversized fixture exceeds the stream request limit');
      check((await api(route,{raw:oversized,auth:token})).status===413,route+' rejects known-length bodies over 4 KiB');
      check(await chunked(route,oversized,token)===413,route+' rejects chunked bodies over 4 KiB using the pinned fixture certificate');
    }

    const after=(await api('/api/computer/display',{auth:token})).data;
    check(!after.active&&!after.sharedWithTablet,'all stream boundary requests leave display creation and sharing off');
    const device=(await api('/api/admin/info')).data.devices.find(value=>value.name===name);
    assert.ok(device,'paired fixture device is present before revocation');
    check((await api('/api/admin/revoke',{body:{id:device.id}})).status===200,'local PC can revoke the fixture credential');
    for(const [route,body] of endpoints){
      check((await api(route,{body,auth:token})).status===401,route+' refuses a revoked device credential');
    }
    console.log(passed+' display-stream API checks passed; no real display creation, sharing, or pixel capture occurred.');
  }finally{
    if(child&&child.exitCode===null){
      try{await api('/api/admin/shutdown',{body:{},timeout:3000});}catch{}
      if(child.exitCode===null){
        await Promise.race([new Promise(resolve=>child.once('exit',resolve)),sleep(3000)]);
        if(child.exitCode===null)child.kill();
      }
    }
    if(log!==undefined){fs.closeSync(log);log=undefined;}
  }
}
run().catch(error=>{console.error(error);console.error('Isolated fixture logs: '+fixture);process.exitCode=1;}).finally(()=>{if(child&&child.exitCode===null)child.kill();});
