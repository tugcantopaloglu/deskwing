// Isolated API and UI-logic regression checks. No browser, real OBS, or audio.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),https=require('node:https'),vm=require('node:vm'),acorn=require('acorn'),{spawn}=require('node:child_process');
const root=path.resolve(__dirname,'..'),output=path.join(process.env.TABLETDECK_TEST_OUTPUT||path.join(root,'.test-output'),'execution');fs.mkdirSync(output,{recursive:true});
const executable=process.env.TABLETDECK_COMPANION||path.join(root,'companion/bin/Release/net10.0-windows/win-x64/DeskWing-Companion.exe');
let server,passed=0;const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms)),check=(value,label)=>{assert.ok(value,label);passed++;console.log('PASS '+label);};
async function run(){
 const data=fs.mkdtempSync(path.join(output,'data-')),log=fs.openSync(path.join(output,'server.log'),'w');
 server=spawn(executable,['--no-browser','--no-tray','--data-dir',data,'--http-port','19040','--https-port','19043'],{windowsHide:true,stdio:['ignore',log,log]});
 let session;for(let attempt=0;attempt<80;attempt++){try{session=JSON.parse(fs.readFileSync(path.join(data,'admin-session.json'),'utf8'));if((await fetch('http://127.0.0.1:19040/api/board',{headers:{'X-Deck-Admin':session.token}})).ok)break;session=null;}catch{}await sleep(100);}assert.ok(session);
 async function admin(route,body){const response=await fetch('http://127.0.0.1:19040'+route,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json','X-Deck-Admin':session.token},body:body===undefined?undefined:JSON.stringify(body)});return{status:response.status,data:await response.json()};}
 function tablet(route,body,token=''){return new Promise((resolve,reject)=>{const request=https.request({host:'127.0.0.1',port:19043,path:route,method:body===undefined?'GET':'POST',rejectUnauthorized:false,headers:{'Content-Type':'application/json',...(token?{Authorization:'Bearer '+token}:{})}},response=>{let text='';response.on('data',chunk=>text+=chunk);response.on('end',()=>resolve({status:response.statusCode,data:JSON.parse(text)}));});request.on('error',reject);request.end(body===undefined?undefined:JSON.stringify(body));});}
 check((await tablet('/api/execution')).status===401&&(await tablet('/api/cancel',{id:'x'})).status===401,'execution status and cancellation require authentication');
 const pairing=(await admin('/api/admin/pairing',{})).data,token=(await tablet('/api/pair',{code:pairing.code,name:'Execution fixture'})).data.token;
 const config=(await admin('/api/admin/config')).data;
 const delay=(label,ms)=>({label,kind:'delay',target:String(ms)});
 config.pages[0].buttons=[{id:'macro',label:'Private-safe progress',kind:'macro',steps:[delay('First',0),{label:'Nested',kind:'macro',steps:[delay('Waiting',10000),delay('Never',10000)]}]},{id:'next',label:'Next',kind:'delay',target:'0'},{id:'fail',label:'Missing fixture sound',kind:'sound',target:path.join(data,'missing-sound.wav')},{id:'confirmed',label:'Protected',kind:'macro',steps:[{kind:'stream',label:'Stream'}]}];
 check((await admin('/api/admin/config',{config})).status===200,'nested macro fixture saves');
 check((await tablet('/api/action',{id:'confirmed'},token)).status===409,'macro broadcast confirmation remains required');
 async function waitForStep(){for(let attempt=0;attempt<100;attempt++){const status=(await tablet('/api/execution',undefined,token)).data;if(status.active?.step===2)return status;await sleep(20);}throw Error('Macro did not reach wait step');}
 const pending=tablet('/api/action',{id:'macro'},token),status=await waitForStep(),active=status.active;
 check(active.total===3&&active.stepLabel==='Waiting'&&active.kind==='macro','nested progress counts leaf steps and shows current label');
 check(!JSON.stringify(status).includes('target')&&!JSON.stringify(status).includes('10000'),'progress exposes no configured targets or PC paths');
 check((await tablet('/api/action',{id:'next'},token)).status===409,'concurrent actions are refused without replacing the running macro');
 check((await tablet('/api/cancel',{id:'bad'},token)).status===400,'malformed cancellation identity is rejected');
 check(!(await tablet('/api/cancel',{id:'a'.repeat(32)},token)).data.cancelled&&(await tablet('/api/execution',undefined,token)).data.active.id===active.id,'wrong run identity cannot cancel a running macro');
 const started=Date.now(),cancel=await tablet('/api/cancel',{id:active.id},token),result=await pending;
 check(cancel.data.cancelled&&result.status===409&&result.data.error.includes('cancelled')&&Date.now()-started<1500,'HTTPS cancellation interrupts a ten-second wait promptly');
 check((await tablet('/api/execution',undefined,token)).data.active===null,'cancelled run releases execution state');
 check(!(await tablet('/api/cancel',{id:active.id},token)).data.cancelled,'repeated cancellation is harmless');
 const second=tablet('/api/action',{id:'macro'},token),newStatus=await waitForStep();
 check(newStatus.active.id!==active.id&&newStatus.revision>status.revision,'each run has a new identity and increasing revision');
 check(!(await tablet('/api/cancel',{id:active.id},token)).data.cancelled,'stale cancellation cannot stop a newer run');
 await tablet('/api/cancel',{id:newStatus.active.id},token);await second;
 check((await tablet('/api/action',{id:'fail'},token)).status===400&&(await tablet('/api/execution',undefined,token)).data.active===null,'failed action clears progress and releases execution slot');
 check((await tablet('/api/action',{id:'next'},token)).data.ok&&(await tablet('/api/execution',undefined,token)).data.active===null,'successful next action runs and clears progress');

 const nodes=new Map();function $(id){if(!nodes.has(id))nodes.set(id,{hidden:true,textContent:'',disabled:false});return nodes.get(id);}
 const classes=new Set(),requests=[];let reply={cancelled:true};
 const context=vm.createContext({$,document:{body:{classList:{toggle:(name,on)=>on?classes.add(name):classes.delete(name)}}},executionStatus:null,executionPending:'Pending macro',executionReachable:true,cancelPending:false,busy:false,api:async(route,body)=>{requests.push({route,body});return route==='/api/execution'?{session:'ui',revision:10,active:null}:reply;},notice(){}});
 const source=fs.readFileSync(path.join(root,'companion/wwwroot/app.js'),'utf8'),ast=acorn.parse(source,{ecmaVersion:'latest'});
 for(const node of ast.body){if(node.type==='FunctionDeclaration'&&['acceptExecution','renderExecution','pollExecutionOnce'].includes(node.id.name))vm.runInContext(source.slice(node.start,node.end),context);if(node.type==='ExpressionStatement'&&source.slice(node.start,node.end).startsWith("$('#cancel-action').onclick="))vm.runInContext(source.slice(node.start,node.end),context);}
 context.status={session:'ui',revision:3,active:{id:'b'.repeat(32),label:'<img src=x onerror=alert(1)>',step:2,total:3,stepLabel:'Waiting',cancelling:false}};
 vm.runInContext('acceptExecution(status)',context);
 check(!$('#execution').hidden&&$('#execution-label').textContent===context.status.active.label&&$('#execution-step').textContent==='Step 2 of 3 · Waiting','UI displays progress through textContent without interpreting markup');
 check(classes.has('execution-visible')&&!$('#cancel-action').disabled,'running action reserves a separate ribbon and enables cancel');
 vm.runInContext("acceptExecution({session:'ui',revision:2,active:null})",context);check(!$('#execution').hidden,'late status cannot hide a newer running action');
 vm.runInContext('executionReachable=false;acceptExecution(status)',context);check($('#execution-step').textContent.includes('Connection lost'),'cached same-revision state cannot clear a lost-connection warning');
 vm.runInContext('acceptExecution(status,true)',context);check(!$('#execution-step').textContent.includes('Connection lost'),'fresh successful status clears a lost-connection warning');
 context.status.active.cancelling=true;context.status.revision=4;vm.runInContext('acceptExecution(status)',context);check($('#cancel-action').disabled&&$('#execution-step').textContent.includes('Completed steps are kept'),'cancelling state disables repeat taps and explains preserved steps');
 context.status.active.cancelling=false;context.status.revision=5;vm.runInContext('acceptExecution(status)',context);await $('#cancel-action').onclick();
 check(requests[0].route==='/api/cancel'&&requests[0].body.id==='b'.repeat(32),'cancel control sends the displayed run identity');
 check($('#execution').hidden&&!classes.has('execution-visible'),'idle status returns space to the board');
 vm.runInContext("busy=true;executionStatus=null;executionReachable=false;renderExecution()",context);check(!$('#execution').hidden&&$('#cancel-action').disabled&&$('#execution-step').textContent.includes('may still be running'),'lost connection avoids false success and prevents cancellation without a run identity');
 await admin('/api/admin/shutdown',{});await new Promise(resolve=>server.once('exit',resolve));server=null;fs.closeSync(log);
 fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({passed,browserOpened:false,guiOpened:false,realObsControlled:false},null,2));console.log(passed+' execution API/UI-logic checks passed.');
}
run().catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>{if(server)server.kill();});
