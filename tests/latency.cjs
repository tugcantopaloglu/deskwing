// Synthetic local benchmark. Does not open OBS, a browser, or any GUI.
const fs=require('node:fs'),path=require('node:path'),https=require('node:https'),tls=require('node:tls'),{performance}=require('node:perf_hooks'),{spawn}=require('node:child_process'),{WebSocketServer}=require('ws');
const root=path.resolve(__dirname,'..'),folder=path.join(root,'.test-output','latency');fs.mkdirSync(folder,{recursive:true});
const executable=process.env.TABLETDECK_COMPANION||path.resolve(root,'../../outputs/TabletDeck-Studio-1.3/Windows/DeskWing-Companion.exe');
const label=process.env.TABLETDECK_BENCH_LABEL||'baseline';
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));let server,service,extraDelay=0,scene='Gameplay';
const summary=values=>{values.sort((a,b)=>a-b);return {samples:values.length,p50ms:+values[Math.floor(values.length*.5)].toFixed(3),p95ms:+values[Math.floor(values.length*.95)].toFixed(3),maxms:+values.at(-1).toFixed(3)};};
async function run(){
 const mock=new WebSocketServer({host:'127.0.0.1',port:19555});service=mock;
 mock.on('connection',socket=>{socket.send(JSON.stringify({op:0,d:{rpcVersion:1}}));socket.on('message',async buffer=>{const msg=JSON.parse(buffer);if(msg.op===1){socket.send(JSON.stringify({op:2,d:{negotiatedRpcVersion:1}}));return;}const {requestType:type,requestId,requestData:data={}}=msg.d;let response={};
 if(type==='SetCurrentProgramScene')scene=data.sceneName;
 if(type==='GetSceneList')response={currentProgramSceneName:scene,scenes:[{sceneName:'Gameplay'}]};
 if(type==='GetStreamStatus'||type==='GetRecordStatus')response={outputActive:false};if(type==='GetInputVolume')response={inputVolumeDb:-6};if(type==='GetInputMute')response={inputMuted:false};
 if(extraDelay)await delay(extraDelay);socket.send(JSON.stringify({op:7,d:{requestId,requestType:type,requestStatus:{result:true,code:100},responseData:response}}));});});
 const data=fs.mkdtempSync(path.join(folder,'data-'));const log=fs.openSync(path.join(folder,label+'.log'),'w');
 server=spawn(executable,['--no-browser','--no-tray','--data-dir',data,'--http-port','18840','--https-port','18843'],{windowsHide:true,stdio:['ignore',log,log]});
 let session;for(let attempt=0;attempt<80;attempt++){try{session=JSON.parse(fs.readFileSync(path.join(data,'admin-session.json'),'utf8'));const r=await fetch('http://127.0.0.1:18840/api/board',{headers:{'X-Deck-Admin':session.token}});if(r.ok)break;session=null;}catch{}await delay(100);}if(!session)throw Error('Companion not ready');
 const admin=async(route,body)=>{const r=await fetch('http://127.0.0.1:18840'+route,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json','X-Deck-Admin':session.token},body:body===undefined?undefined:JSON.stringify(body)});if(!r.ok)throw Error(await r.text());return r.json();};
 const pin=await admin('/api/admin/pairing',{});
 const request=(route,body,token='')=>new Promise((resolve,reject)=>{const req=https.request({host:'127.0.0.1',port:18843,path:route,method:body===undefined?'GET':'POST',agent:false,rejectUnauthorized:false,headers:{'Content-Type':'application/json',...(token?{Authorization:'Bearer '+token}:{})}},res=>{let text='';res.on('data',chunk=>text+=chunk);res.on('end',()=>{if(res.statusCode!==200)return reject(Error(text));resolve(JSON.parse(text));});});req.on('error',reject);req.end(body===undefined?undefined:JSON.stringify(body));});
 const pair=await request('/api/pair',{code:pin.code,name:'Latency fixture'});const config=await admin('/api/admin/config');config.obsPort=19555;await admin('/api/admin/config',{config,replacePassword:false});const action=config.pages[0].buttons[0].id;
 await admin('/api/state');for(let i=0;i<15;i++)await request('/api/action',{id:action},pair.token);
 const cold=[];for(let i=0;i<100;i++){const start=performance.now();await request('/api/action',{id:action},pair.token);cold.push(performance.now()-start);}
 const state=[];extraDelay=12;
 for(let i=0;i<25;i++){await admin('/api/action',{id:action});const start=performance.now();await admin('/api/state');state.push(performance.now()-start);}
 extraDelay=0;
 const result={label,qualification:'Loopback Windows fixture; cold HTTPS creates a new TLS connection for each action; state fixture adds 12ms per OBS response. Not Wi-Fi, USB, Android touch, audio output, or a live stream.',coldTlsAction:summary(cold),stateWith12msObsResponse:summary(state)};
 if(process.env.TABLETDECK_BENCH_FAST==='1'){
  const socket=tls.connect({host:'127.0.0.1',port:18844,rejectUnauthorized:false});socket.setNoDelay(true);let buffer=Buffer.alloc(0),next=0;const pending=new Map();let ready;
  const readyTask=new Promise((resolve,reject)=>{ready=resolve;socket.on('error',reject);});
  const send=value=>{const bytes=Buffer.from(JSON.stringify(value)),header=Buffer.alloc(4);header.writeUInt32BE(bytes.length);socket.write(Buffer.concat([header,bytes]));};
  socket.on('data',chunk=>{buffer=Buffer.concat([buffer,chunk]);while(buffer.length>=4&&buffer.length>=4+buffer.readUInt32BE()){const count=buffer.readUInt32BE(),message=JSON.parse(buffer.subarray(4,4+count));buffer=buffer.subarray(4+count);if(message.type==='ready')ready();if(message.id){const request=pending.get(message.id);pending.delete(message.id);message.error?request?.reject(Error(message.error)):request?.resolve(message.result);}}});
  await new Promise((resolve,reject)=>{socket.once('secureConnect',resolve);socket.once('error',reject);});send({token:pair.token});await readyTask;
  const call=(route,body)=>new Promise((resolve,reject)=>{const id=++next;pending.set(id,{resolve,reject});send({id,path:route,body});});
  for(let i=0;i<15;i++)await call('/api/action',{id:action});const values=[];for(let i=0;i<100;i++){const start=performance.now();await call('/api/action',{id:action});values.push(performance.now()-start);}result.persistentTlsAction=summary(values);socket.destroy();
 }
 fs.writeFileSync(path.join(folder,label+'.json'),JSON.stringify(result,null,2));console.log(JSON.stringify(result,null,2));await admin('/api/admin/shutdown',{});await new Promise(resolve=>server.once('exit',resolve));server=null;fs.closeSync(log);
}
run().catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{if(server)server.kill();if(service){for(const socket of service.clients)socket.terminate();service.close();}});
