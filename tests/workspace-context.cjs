// Executable bridge contract fixtures. No browser, sockets, device or private user documents.
'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),acorn=require('acorn');
const source=fs.readFileSync(path.join(__dirname,'../companion/wwwroot/app.js'),'utf8'),tree=acorn.parse(source,{ecmaVersion:'latest'});
const selected=new Set(['workspaceDocumentApiPath','validPairingId','workspaceContextEvent','observeNativeConnection','nativeRequest','api','captureWorkspaceContext','apiWithWorkspaceContext','acceptWorkspaceContext']);
const snippets=tree.body.filter(node=>node.type==='FunctionDeclaration'&&selected.has(node.id.name)||node.type==='ExpressionStatement'&&/^window\.(?:nativeResult|addEventListener)/.test(source.slice(node.start,node.end))&&source.slice(node.start,node.end).includes('deskwing-connection-changed')||node.type==='ExpressionStatement'&&source.slice(node.start,node.end).startsWith('window.nativeResult=')).map(node=>source.slice(node.start,node.end));
const A='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',B='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',C='cccccccc-cccc-4ccc-8ccc-cccccccccccc';
let current=A,checks=0,holdConnection=false,holdNotebook=false;
const sent=[],committed=[],held=[],listeners=new Map(),events=[];
class FixtureEvent{constructor(type,init={}){this.type=type;this.detail=init.detail;}}
const window={addEventListener(type,fn){listeners.set(type,[...(listeners.get(type)||[]),fn]);},dispatchEvent(event){events.push(event);for(const fn of listeners.get(event.type)||[])fn(event);return true;}};
window.DeckNative={dispatch(raw){
  const request=JSON.parse(raw);sent.push(request);
  if(request.path==='/native/connection'){
    if(holdConnection){held.push(()=>window.nativeResult(request.id,{paired:true,pairingId:current}));return;}
    window.nativeResult(request.id,{paired:true,pairingId:current});return;
  }
  if(request.path.startsWith('/api/notebooks')||request.path==='/api/ink'||request.path.startsWith('/api/ink?')){
    // Models the separately tested Android/iOS pre-HTTP contract. The field is
    // checked before the receiver can construct or submit the HTTP body.
    if(request.expectedPairingId!==current){window.nativeResult(request.id,{error:'Notebook pairing changed before HTTP commit.'});return;}
    if(holdNotebook){held.push(()=>window.nativeResult(request.id,{library:'original PC response'}));return;}
    committed.push({pairingId:current,body:request.body,method:request.method});window.nativeResult(request.id,{ok:true});return;
  }
  throw new Error('Unexpected fixture path');
}};
const context=vm.createContext({console,window,CustomEvent:FixtureEvent,Map,Set,Promise,JSON,Error,setTimeout,clearTimeout,native:true,location:{origin:'http://127.0.0.1:8740'},AbortSignal,fetch:async()=>({ok:true,json:async()=>({ok:true})})});
vm.runInContext("let nextRequest=0;const pending=new Map();let observedPairingId=null,workspaceConnectionEpoch=0,workspaceContext=null,adminToken='fixture';",context);
vm.runInContext(snippets.join('\n'),context);
const evaluate=code=>vm.runInContext(code,context);
const check=(value,label)=>{assert.ok(value,label);checks++;console.log('PASS '+label);};
async function rejects(promise,label){await assert.rejects(promise);check(true,label);}
async function main(){
  check(await evaluate('captureWorkspaceContext()')===A,'catalog intent captures the original opaque native pairing identity');
  const count=sent.length;
  current=B; // Native Settings change before the JavaScript event can be delivered.
  await rejects(evaluate(`apiWithWorkspaceContext('/api/notebooks',{operation:'save',privateDocument:'PC A notes'},'${A}')`),'native Settings switch before JS notification rejects the old document before HTTP commit');
  check(sent.length===count+1&&sent.at(-1).expectedPairingId===A,'envelope retains the original identity rather than attaching the newest PC');
  check(!Object.hasOwn(sent.at(-1).body,'expectedPairingId')&&committed.length===0,'pairing guard stays outside the HTTP body and no old content is committed');
  await rejects(evaluate(`apiWithWorkspaceContext('/api/ink',{expectedRevision:'old revision',document:{notes:'PC A private ink'}},'${A}')`),'legacy ink saved under the old pairing is rejected before any new-PC HTTP body is committed');
  check(sent.at(-1).expectedPairingId===A&&committed.length===0,'legacy ink also retains its snapshot pairing identity when native Settings change before notification');
  window.dispatchEvent(new FixtureEvent('deskwing-connection-changed',{detail:{reason:'pair',generation:'2'}}));
  const beforeMissing=sent.length;
  await rejects(evaluate("api('/api/notebooks',{operation:'save'})"),'generic native notebook requests require an explicitly retained pairing context');
  check(sent.length===beforeMissing,'missing notebook context fails before crossing the native bridge');
  const beforeMissingInk=sent.length;
  await rejects(evaluate("api('/api/ink',{expectedRevision:'old',document:{notes:'private'}})"),'legacy ink writes also require an explicitly retained pairing context');
  check(sent.length===beforeMissingInk,'unbound legacy content is rejected before the native bridge');
  check(await evaluate('captureWorkspaceContext()')===B,'a fresh post-change catalog intent captures the new PC');
  await evaluate(`apiWithWorkspaceContext('/api/notebooks',undefined,'${B}')`);
  check(committed.length===1&&committed[0].pairingId===B&&committed[0].method==='GET','bound catalog reads use their intended current PC');
  await evaluate(`apiWithWorkspaceContext('/api/ink?revision=${'A'.repeat(64)}',undefined,'${B}')`);
  check(committed.length===2&&committed[1].pairingId===B&&committed[1].method==='GET','legacy revision polling is bound to the captured PC');
  await evaluate(`apiWithWorkspaceContext('/api/notebooks',{operation:'save'},'${B}')`);
  check(committed.length===3&&committed[2].pairingId===B,'ordinary activity pauses cannot silently replace the retained pairing identity');
  holdNotebook=true;
  const pendingRead=evaluate(`apiWithWorkspaceContext('/api/notebooks',undefined,'${B}')`);
  current=C;window.dispatchEvent(new FixtureEvent('deskwing-connection-changed',{detail:{reason:'transport',generation:'3'}}));held.shift()();holdNotebook=false;
  await rejects(pendingRead,'late catalog completion is rejected after the connection epoch changes');
  holdConnection=true;
  const pendingCapture=evaluate('captureWorkspaceContext()');
  window.dispatchEvent(new FixtureEvent('deskwing-connection-changed',{detail:{reason:'pair',generation:'4'}}));held.shift()();holdConnection=false;
  await rejects(pendingCapture,'a context capture interrupted by a connection change cannot adopt the newest PC for an old intent');
  check(await evaluate('captureWorkspaceContext()')===C,'explicit reopening can capture the settled connection');
  const beforeOld=sent.length;
  await rejects(evaluate(`apiWithWorkspaceContext('/api/notebooks',{operation:'save'},'${B}')`),'old retained catalog identity is rejected locally after a newer PC is observed');
  check(sent.length===beforeOld,'known stale content does not cross even the JavaScript-to-native bridge');
  const beforeProfile=events.filter(event=>event.type==='deskwing-workspace-contextchange').length;
  evaluate("acceptWorkspaceContext({profileId:'one',workspace:{computerName:'PC',connectionCode:'AAAA-BBBB-CCCC',version:'1'}});acceptWorkspaceContext({profileId:'two',workspace:{computerName:'PC',connectionCode:'AAAA-BBBB-CCCC',version:'1'}})");
  check(events.filter(event=>event.type==='deskwing-workspace-contextchange').length===beforeProfile,'normal board-profile changes do not invalidate the notebook PC context');
  evaluate("acceptWorkspaceContext({workspace:{computerName:'Other PC',connectionCode:'DDDD-EEEE-FFFF',version:'1'}})");
  check(events.filter(event=>event.type==='deskwing-workspace-contextchange').length===beforeProfile+1,'a changed observed PC identity notifies notebook consumers');
  await rejects(evaluate(`apiWithWorkspaceContext('/api/notebooks-copy',{},'${C}')`),'context helper refuses non-notebook API lookalikes');
  await rejects(evaluate(`apiWithWorkspaceContext('/api/ink-copy',{},'${C}')`),'context helper refuses legacy ink API lookalikes');
  context.native=false;
  const web=await evaluate('captureWorkspaceContext()');check(web==='web:http://127.0.0.1:8740','desktop context is retained to the current local origin');
  await evaluate(`apiWithWorkspaceContext('/api/notebooks',undefined,'${web}')`);
  context.location.origin='http://127.0.0.1:9740';
  await rejects(evaluate(`apiWithWorkspaceContext('/api/notebooks',{operation:'save'},'${web}')`),'desktop requests cannot reuse a context from another origin');
  console.log(checks+' workspace context checks passed; native receiver and HTTP delivery are simulated, no network used.');
}
main().catch(error=>{console.error(error);process.exitCode=1;});
