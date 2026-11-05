// Native-pen HTTP boundaries only. The fixture never enables or starts real pen injection.
const assert=require('node:assert/strict'),fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),https=require('node:https'),crypto=require('node:crypto'),{spawn,spawnSync}=require('node:child_process');
const root=path.resolve(__dirname,'..'),fixture=fs.mkdtempSync(path.join(os.tmpdir(),'tabletdeck-pen-api-'));
const executable=process.env.TABLETDECK_COMPANION||path.join(root,'build/backend-review/bin/TabletDeck-Companion.exe');
let child,session,passed=0;const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
function check(condition,label){assert.ok(condition,label);passed++;console.log('PASS '+label);}
async function port(){const server=net.createServer();await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));const value=server.address().port;await new Promise(resolve=>server.close(resolve));return value;}
function fixtureTls(){
 const script='$ErrorActionPreference="Stop"; $certificate=[System.Security.Cryptography.X509Certificates.X509Certificate2]::new([IO.File]::ReadAllBytes($env:DESKWING_PEN_TEST_PFX),$null,[System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet); try { [Console]::Write([Convert]::ToBase64String($certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))) } finally { $certificate.Dispose() }';
 const result=spawnSync('powershell.exe',['-NoProfile','-NonInteractive','-Command',script],{windowsHide:true,encoding:'utf8',timeout:15000,env:{...process.env,DESKWING_PEN_TEST_PFX:path.join(fixture,'companion.pfx')}});
 if(result.error||result.status!==0)throw Error('Could not read the isolated test certificate: '+(result.error?.message||result.stderr));
 const certificate=new crypto.X509Certificate(Buffer.from(result.stdout.trim(),'base64'));
 return {ca:certificate.toString(),checkServerIdentity:(_host,peer)=>peer.raw?.equals(certificate.raw)?undefined:Error('The test companion certificate does not match its private fixture.')};
}
async function run(){
 const chosen=new Set();for(let attempt=0;chosen.size<3&&attempt<100;attempt++)chosen.add(await port());assert.equal(chosen.size,3,'three distinct isolated ports are available');
 const [httpPort,httpsPort,controlPort]=[...chosen];
 const log=fs.openSync(path.join(fixture,'service.log'),'w');
 child=spawn(executable,['--no-browser','--no-tray','--data-dir',fixture,'--http-port',String(httpPort),'--https-port',String(httpsPort),'--control-port',String(controlPort)],{windowsHide:true,stdio:['ignore',log,log]});
 async function api(route,body,headers={}){const response=await fetch(`http://127.0.0.1:${httpPort}${route}`,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json','X-Deck-Admin':session?.token||'',...headers},body:body===undefined?undefined:JSON.stringify(body)});const text=await response.text();let data;try{data=text?JSON.parse(text):{};}catch{data={};}return{status:response.status,data};}
 try{
  for(let i=0;i<100;i++){try{session=JSON.parse(fs.readFileSync(path.join(fixture,'admin-session.json'),'utf8'));if((await api('/api/pen')).status===200)break;}catch{}await sleep(100);}
  assert.ok(session,'isolated companion starts');
  check((await api('/api/pen',undefined,{'X-Deck-Admin':''})).status===401,'native pen status requires authentication');
  check((await api('/api/pen',{operation:'begin'}, {'X-Deck-Admin':''})).status===401,'native pen writes require authentication');
  const pin=(await api('/api/admin/pairing',{})).data;
  const pair=await api('/api/pair',{code:pin.code,name:'Isolated pen fixture'},{'X-Deck-Admin':''});assert.equal(pair.status,200);
  const tablet={'X-Deck-Admin':'',Authorization:'Bearer '+pair.data.token};
  const status=(await api('/api/pen',undefined,tablet)).data;
  check(status.enabled===false&&status.active===false&&typeof status.supported==='boolean','PC pen is disabled and inactive by default');
  check(!JSON.stringify(status).includes(pair.data.token)&&!('lease'in status),'status exposes no device credential or active lease token');
  check((await api('/api/admin/pen',undefined,tablet)).status===403,'tablet cannot enumerate PC target windows');
  check((await api('/api/admin/pen',{enabled:true,targetId:'invented'},tablet)).status===403,'tablet cannot enable native pen or select its PC target');
  check((await api('/api/pen',{operation:'begin',session:crypto.randomUUID()},tablet)).status===409,'begin is rejected before injection until local PC opt-in');
  check((await api('/api/pen',{operation:'samples',lease:'missing',sequence:1,elapsedMs:0,samples:[{phase:'down',x:.5,y:.5,pressure:.5}]},tablet)).status===409,'unowned samples cannot enter native input');
  check((await api('/api/pen',{},tablet)).status===400,'missing pen operation is refused safely');
  check((await api('/api/pen',{operation:'end',lease:'expired'},tablet)).status===200,'late end is idempotent without an active pen');
  check((await api('/api/pen',{operation:'cancel',lease:'expired'},tablet)).status===200,'late cancellation is idempotent without an active pen');
  check((await api('/api/pen',{operation:'begin',session:crypto.randomUUID()},{...tablet,Origin:'https://external.invalid'})).status===403,'cross-origin pen writes are rejected');
  check((await api('/api/admin/pen',{enabled:true,targetId:'invented'},{Origin:'https://external.invalid'})).status===403,'cross-origin PC opt-in is rejected');
  check((await api('/api/admin/pen',{enabled:false})).status===200,'local PC can explicitly disable an inactive service');
  const oversized=JSON.stringify({operation:'cancel',lease:'expired',padding:'x'.repeat(17000)});
  const large=await fetch(`http://127.0.0.1:${httpPort}/api/pen`,{method:'POST',headers:{...tablet,'Content-Type':'application/json'},body:oversized});await large.text();
  check(large.status===413,'known-length pen bodies above16KB are rejected before binding');
  const streamed=await new Promise((resolve,reject)=>{const request=https.request({host:'127.0.0.1',port:httpsPort,path:'/api/pen',...fixtureTls(),method:'POST',headers:{Authorization:tablet.Authorization,'Content-Type':'application/json','Transfer-Encoding':'chunked'}},response=>{response.resume();response.on('end',()=>resolve(response.statusCode));});request.on('error',reject);request.setTimeout(15000,()=>request.destroy(Error('The chunked pen fixture timed out.')));request.write(oversized.slice(0,8000));request.end(oversized.slice(8000));});
  check(streamed===413,'streamed pen bodies above16KB are rejected before binding');
  check((await api('/api/pen',undefined,tablet)).data.active===false,'all boundary tests leave native pen inactive');
  check(!(await api('/api/admin/computer')).data.enabled,'native pen requests do not enable other PC control modes');
  console.log(passed+' pen API checks passed; native input never enabled.');
 }finally{
  if(child&&child.exitCode===null){try{await api('/api/admin/shutdown',{});}catch{}await Promise.race([new Promise(resolve=>child.once('exit',resolve)),sleep(3000)]);if(child.exitCode===null)child.kill();}
  fs.closeSync(log);
 }
}
run().catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{if(child&&child.exitCode===null)child.kill();});
