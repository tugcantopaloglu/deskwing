'use strict';
// All pen transport here is fake. This suite never calls the companion or injects Windows input.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const {webcrypto} = require('node:crypto');
const source = path.resolve(__dirname,'../companion/wwwroot/pen-tablet.js');
const core = require(source);
let passed = 0;
const drain = async () => {for(let i=0;i<10;i++)await Promise.resolve();};
const deferred = () => {let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b;});return {promise,resolve,reject};};
function clock() {
  let time=0,next=0;const jobs=new Map();
  return {now:()=>time,schedule:(fn,delay)=>{const id=++next;jobs.set(id,{fn,at:time+delay});return id;},unschedule:id=>jobs.delete(id),
    async advance(ms){const until=time+ms;for(let guard=0;guard<20000;guard++){const pending=[...jobs].filter(([,job])=>job.at<=until).sort((a,b)=>a[1].at-b[1].at)[0];if(!pending)break;jobs.delete(pending[0]);time=pending[1].at;pending[1].fn();await drain();}time=until;await drain();},get pending(){return jobs.size;}};
}
function harness(handler) {
  const timer=clock(),calls=[];let leases=0;
  const client=core.createClient({...timer,request:async packet=>{calls.push(structuredClone(packet));if(handler){const result=handler(packet,calls);if(result!==undefined)return result;}if(packet.operation==='begin')return {lease:'lease-'+(++leases),sequence:0,maxBatch:32,expiresInMs:1200};return {ok:true,sequence:packet.sequence,expiresInMs:1200};}});
  return {timer,calls,client};
}
const point=(phase='move',x=.4)=>({phase,x,y:.6,pressure:.7,tiltX:20,tiltY:-10,barrel:false,eraser:false});
const begin=client=>client.start('0123456789abcdef0123456789abcdef');
async function check(label,test){await test();passed++;console.log('PASS '+label);}

async function main(){
await check('normalized pen coordinates preserve pressure and tilt',()=>{
  const result=core.makeSample({pointerType:'pen',clientX:60,clientY:120,pressure:.8,tiltX:30,tiltY:-40,buttons:1,button:0},{left:10,top:20,width:200,height:200},'down');
  assert.deepEqual(result,{phase:'down',x:.25,y:.5,pressure:.8,tiltX:30,tiltY:-40,barrel:false,eraser:false});
});
await check('touch and mouse input never become Windows pen samples',()=>{
  for(const pointerType of ['touch','mouse',''])assert.equal(core.makeSample({pointerType,clientX:1,clientY:1},{left:0,top:0,width:10,height:10},'down'),null);
});
await check('barrel-only hover is not tip contact or an eraser',()=>{
  const event={pointerType:'pen',button:2,buttons:2,pressure:0,clientX:2,clientY:3};
  assert.equal(core.isContact(event),false);
  const sample=core.makeSample(event,{left:0,top:0,width:10,height:10},'hover');assert.equal(sample.barrel,true);assert.equal(sample.eraser,false);assert.equal(sample.pressure,0);
  assert.equal(core.isContact({...event,button:0,buttons:0}),false);
});
await check('inverted pen reports eraser separately from barrel',()=>{
  const event={pointerType:'pen',button:5,buttons:32,pressure:.4,clientX:2,clientY:3};assert.equal(core.isContact(event),true);
  const sample=core.makeSample(event,{left:0,top:0,width:10,height:10},'down');assert.equal(sample.eraser,true);assert.equal(sample.barrel,false);
});
await check('out-of-bounds captured coordinates and hardware values remain finite',()=>{
  const sample=core.normalizeSample({...point(),x:-20,y:4,pressure:2,tiltX:200,tiltY:-200});assert.equal(sample.x,0);assert.equal(sample.y,1);assert.equal(sample.pressure,1);assert.equal(sample.tiltX,90);assert.equal(sample.tiltY,-90);
  for(const phase of ['hover','up','leave'])assert.equal(core.normalizeSample(point(phase)).pressure,0);
  assert.throws(()=>core.normalizeSample({...point(),pressure:NaN}));
});
await check('opening or enqueuing without explicit Start sends no input',async()=>{
  const h=harness();assert.equal(h.client.enqueue(point('down')),false);await h.timer.advance(1000);assert.equal(h.calls.length,0);
});
await check('small framed batches preserve contact order and sequence',async()=>{
  const h=harness();assert.equal(await begin(h.client),true);h.client.enqueue(point('down'));h.client.enqueue(point('move'));h.client.enqueue(point('up'));await h.timer.advance(24);
  const packet=h.calls.find(x=>x.operation==='samples');assert.deepEqual(packet.samples.map(x=>x.phase),['down','move','up']);assert.equal(packet.sequence,1);assert.equal(packet.elapsedMs,24);assert.equal(h.client.snapshot().inFlight,false);await h.client.stop();
});
await check('one pending data request bounds backpressure and prevents overlapping renewals',async()=>{
  const held=deferred();const h=harness(packet=>packet.operation==='samples'?held.promise:undefined);await begin(h.client);h.client.enqueue(point('down'));await h.timer.advance(24);
  for(let i=0;i<500;i++)h.client.enqueue(point('move',i/500));h.client.enqueue(point('up'));
  assert.ok(h.client.snapshot().pending<=core.constants.MAX_PENDING);await h.timer.advance(350);assert.equal(h.calls.filter(x=>['samples','renew'].includes(x.operation)).length,1);
  held.resolve({ok:true,sequence:1});await drain();await h.timer.advance(120);const batches=h.calls.filter(x=>x.operation==='samples');assert.ok(batches.every(x=>x.samples.length<=32));await h.client.stop();
});
await check('queue compaction keeps down and up in their original order',async()=>{
  const h=harness();await begin(h.client);h.client.enqueue(point('down'));for(let i=0;i<500;i++)h.client.enqueue(point('move'));h.client.enqueue(point('up'));await h.timer.advance(100);
  const phases=h.calls.filter(x=>x.operation==='samples').flatMap(x=>x.samples.map(s=>s.phase));assert.equal(phases[0],'down');assert.equal(phases.at(-1),'up');assert.equal(phases.filter(x=>x==='down').length,1);await h.client.stop();
});
await check('transition-only queue overflow stops rather than losing a pen-up',async()=>{
  const h=harness();await begin(h.client);for(let i=0;i<core.constants.MAX_PENDING+1;i++)h.client.enqueue(point(i%2?'up':'down'));await drain();assert.equal(h.client.active,false);assert.equal(h.client.snapshot().pending,0);assert.ok(h.calls.some(x=>x.operation==='cancel'));
});
await check('stale queued input cancels and is never replayed',async()=>{
  const held=deferred();const h=harness(packet=>packet.operation==='samples'?held.promise:undefined);await begin(h.client);h.client.enqueue(point('down'));await h.timer.advance(24);h.client.enqueue(point('move'));await h.timer.advance(528);
  assert.equal(h.client.active,false);assert.equal(h.client.snapshot().pending,0);assert.match(h.client.snapshot().message,/too slow/);held.resolve({ok:true,sequence:1});await drain();await h.timer.advance(200);assert.equal(h.calls.filter(x=>x.operation==='samples').length,1);
});
await check('renewals keep an idle lease alive without fabricating pen samples',async()=>{
  const h=harness();await begin(h.client);await h.timer.advance(360);const packet=h.calls.find(x=>x.operation==='renew');assert.ok(packet);assert.equal(packet.samples.length,0);assert.equal(packet.sequence,1);await h.client.stop();
});
await check('wrong acknowledgement sequence fails closed',async()=>{
  const h=harness(packet=>packet.operation==='samples'?{ok:true,sequence:999}:undefined);await begin(h.client);h.client.enqueue(point('down'));await h.timer.advance(24);assert.equal(h.client.active,false);assert.match(h.client.snapshot().message,/confirm/);assert.ok(h.calls.some(x=>x.operation==='cancel'));
});
await check('request timeout releases the current lease and clears queued input',async()=>{
  const held=deferred();const h=harness(packet=>packet.operation==='samples'?held.promise:undefined);await begin(h.client);h.client.enqueue(point('down'));await h.timer.advance(900);assert.equal(h.client.active,false);assert.match(h.client.snapshot().message,/respond in time/);assert.ok(h.calls.some(x=>x.operation==='cancel'&&x.lease==='lease-1'));held.reject(new Error('late disconnect'));await drain();
});
await check('Stop during pending begin releases a late lease without activation',async()=>{
  const held=deferred();const h=harness(packet=>packet.operation==='begin'?held.promise:undefined);const starting=begin(h.client);await drain();await h.client.stop('Stopped by user');held.resolve({lease:'late-lease',sequence:0,maxBatch:32});assert.equal(await starting,false);await drain();assert.equal(h.client.active,false);assert.ok(h.calls.some(x=>x.operation==='cancel'&&x.lease==='late-lease'));
});
await check('timed-out begin also releases its eventual lease',async()=>{
  const held=deferred();const h=harness(packet=>packet.operation==='begin'?held.promise:undefined);const starting=begin(h.client);await drain();await h.timer.advance(850);assert.equal(await starting,false);held.resolve({lease:'timed-out-lease',sequence:0});await drain();assert.equal(h.client.active,false);assert.ok(h.calls.some(x=>x.operation==='cancel'&&x.lease==='timed-out-lease'));
});
await check('old in-flight failures cannot stop a newer session',async()=>{
  const held=deferred();let once=true;const h=harness(packet=>{if(packet.operation==='samples'&&once){once=false;return held.promise;}});await begin(h.client);h.client.enqueue(point('down'));await h.timer.advance(24);await h.client.stop();await begin(h.client);held.reject(new Error('old failure'));await drain();assert.equal(h.client.active,true);h.client.enqueue(point('down'));await h.timer.advance(24);assert.equal(h.calls.filter(x=>x.operation==='samples').at(-1).lease,'lease-2');assert.equal(h.calls.filter(x=>x.operation==='samples').at(-1).sequence,1);await h.client.stop();
});
await check('fast acknowledgements still send at most one batch per frame',async()=>{
  const h=harness();await begin(h.client);for(let i=0;i<90;i++)h.client.enqueue(point(i===0?'down':'move'));await h.timer.advance(23);assert.equal(h.calls.filter(x=>x.operation==='samples').length,0);await h.timer.advance(1);assert.equal(h.calls.filter(x=>x.operation==='samples').length,1);await h.timer.advance(24);assert.equal(h.calls.filter(x=>x.operation==='samples').length,2);await h.client.stop();
});
await check('browser workspace requires Start and ignores palm cancellation',async()=>{
  const ui=fixture();ui.context.DeskPenTablet.setActive(true);await drain();assert.equal(ui.calls.filter(x=>x.body?.operation==='begin').length,0);assert.equal(ui.find('Start pen session').disabled,false);await ui.click('Start pen session');
  ui.pad().fire('pointerdown',event('touch'));await ui.timer.advance(24);assert.equal(ui.calls.filter(x=>x.body?.operation==='samples').length,0);
  ui.pad().fire('pointerdown',event('pen'));ui.pad().fire('pointercancel',event('touch'));assert.equal(ui.context.DeskPenTablet.isBusy(),true);ui.pad().fire('pointerup',event('pen',{pressure:0,buttons:0}));assert.equal(ui.context.DeskPenTablet.isBusy(),true);await ui.timer.advance(24);
  const samples=ui.calls.filter(x=>x.body?.operation==='samples').flatMap(x=>x.body.samples);assert.deepEqual(samples.map(x=>x.phase),['down','up']);ui.context.DeskPenTablet.setActive(false);await drain();assert.ok(ui.calls.some(x=>x.body?.operation==='cancel'));
});
await check('coalesced pen motion includes final coordinates and barrel state',async()=>{
  const ui=fixture();ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Start pen session');ui.pad().fire('pointerdown',event('pen'));
  ui.pad().fire('pointermove',event('pen',{clientX:300,buttons:3,getCoalescedEvents:()=>[{clientX:200,clientY:100,pressure:.6,tiltX:12,tiltY:15,buttons:3,button:-1}]}));await ui.timer.advance(24);
  const samples=ui.calls.filter(x=>x.body?.operation==='samples').flatMap(x=>x.body.samples);assert.equal(samples.at(-1).x,300/ui.pad().clientWidth);assert.equal(samples.at(-1).barrel,true);assert.equal(samples.at(-1).eraser,false);assert.equal(samples.length,3);ui.context.DeskPenTablet.setActive(false);await drain();
});
await check('tip contact and lift while barrel stays held transition on pointermove',async()=>{
  const ui=fixture();ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Start pen session');
  ui.pad().fire('pointerdown',event('pen',{button:2,buttons:2,pressure:0}));
  ui.pad().fire('pointermove',event('pen',{button:0,buttons:3,pressure:.6}));
  ui.pad().fire('pointermove',event('pen',{button:0,buttons:2,pressure:0}));await ui.timer.advance(24);
  const samples=ui.calls.filter(x=>x.body?.operation==='samples').flatMap(x=>x.body.samples);assert.deepEqual(samples.map(x=>x.phase),['hover','down','up']);assert.ok(samples.every(x=>x.barrel&&!x.eraser));assert.equal(ui.context.DeskPenTablet.isBusy(),true);ui.context.DeskPenTablet.setActive(false);await drain();
});
await check('unexpected pen capture loss and native suspend require a fresh Start',async()=>{
  const ui=fixture();ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Start pen session');ui.pad().fire('pointerdown',event('pen'));ui.pad().fire('lostpointercapture',event('pen'));await drain();assert.equal(ui.context.DeskPenTablet.isBusy(),false);
  ui.context.penTabletSuspend();ui.context.penTabletResume();await drain();assert.equal(ui.context.DeskPenTablet.isBusy(),false);assert.equal(ui.calls.filter(x=>x.body?.operation==='begin').length,1);
});
await check('fullscreen fallback keeps exit and stop controls and releases on layout change',async()=>{
  const ui=fixture();ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Start pen session');await ui.click('Fullscreen');assert.equal(ui.context.DeskPenTablet.isBusy(),false);assert.ok(ui.host.classList.contains('pen-tablet-fullscreen'));assert.ok(ui.find('Exit fullscreen'));assert.ok(ui.find('Stop pen'));assert.ok(ui.find('Back to controls'));await ui.click('Exit fullscreen');assert.equal(ui.host.classList.contains('pen-tablet-fullscreen'),false);
});
await check('leaving the mode while fullscreen waits for release cannot reopen fullscreen',async()=>{
  const held=deferred(),ui=fixture({handle:(_path,body)=>body?.operation==='cancel'?held.promise:undefined});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Start pen session');await ui.click('Fullscreen');ui.context.DeskPenTablet.setActive(false);held.resolve({ok:true});await drain();assert.equal(ui.host.classList.contains('pen-tablet-fullscreen'),false);assert.equal(ui.context.DeskPenTablet.isActive(),false);
});
await check('immersive mode fills the entire surface and tools overlay does not change mapping',async()=>{
  const ui=fixture({width:800,height:600});ui.context.DeskPenTablet.setActive(true);await drain();assert.equal(ui.pad().clientHeight,500);await ui.click('Fullscreen');assert.equal(ui.pad().clientWidth,800);assert.equal(ui.pad().clientHeight,600);assert.equal(ui.byClass('pen-tablet-floating-controls').hidden,false);await ui.click('Show tools');assert.ok(ui.host.classList.contains('pen-tablet-tools-open'));assert.equal(ui.pad().clientHeight,600);await ui.click('Hide tools');assert.equal(ui.host.classList.contains('pen-tablet-tools-open'),false);
});
await check('proportional mapping is optional and changing it releases active input',async()=>{
  const ui=fixture({width:800,height:600});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Fullscreen');await ui.click('Start pen');assert.equal(ui.context.DeskPenTablet.isBusy(),true);await ui.click('Show tools');const select=ui.byAttribute('aria-label','Surface mapping');select.value='match';select.fire('change');await drain();assert.equal(ui.context.DeskPenTablet.isBusy(),false);assert.equal(ui.pad().clientWidth,800);assert.equal(ui.pad().clientHeight,500);assert.ok(ui.calls.some(x=>x.body?.operation==='cancel'));select.value='fill';select.fire('change');assert.equal(ui.pad().clientHeight,600);
});
await check('native immersive requests hide and restore system bars',async()=>{
  const ui=fixture({native:true});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Fullscreen');assert.deepEqual(ui.calls.filter(x=>x.path==='/local/fullscreen').map(x=>x.body.enabled),[true]);await ui.click('Exit fullscreen');assert.deepEqual(ui.calls.filter(x=>x.path==='/local/fullscreen').map(x=>x.body.enabled),[true,false]);assert.equal(ui.host.classList.contains('pen-tablet-fullscreen'),false);
});
await check('native Back exits immersive without echoing another native request',async()=>{
  const ui=fixture({native:true});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Fullscreen');await ui.click('Start pen');ui.document.fire('deskwing-fullscreen',{detail:{enabled:false,reason:'back'}});await drain();assert.equal(ui.host.classList.contains('pen-tablet-fullscreen'),false);assert.equal(ui.context.DeskPenTablet.isBusy(),false);assert.equal(ui.calls.filter(x=>x.path==='/local/fullscreen').length,1);
});
await check('native suspend restores system bars and never re-enters immersive automatically',async()=>{
  const ui=fixture({native:true});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Fullscreen');ui.context.penTabletSuspend();await drain();ui.context.penTabletResume();await drain();assert.equal(ui.host.classList.contains('pen-tablet-fullscreen'),false);assert.deepEqual(ui.calls.filter(x=>x.path==='/local/fullscreen').map(x=>x.body.enabled),[true,false]);
});
await check('stale native fullscreen reply cannot reopen tools after mode exit',async()=>{
  const held=deferred(),ui=fixture({native:true,nativeHandle:(_path,body)=>body.enabled?held.promise:undefined});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Fullscreen');assert.equal(ui.find('Start pen').disabled,true);ui.context.DeskPenTablet.setActive(false);held.reject(new Error('late native reply'));await drain();assert.equal(ui.host.classList.contains('pen-tablet-fullscreen'),false);assert.equal(ui.host.classList.contains('pen-tablet-tools-open'),false);assert.deepEqual(ui.calls.filter(x=>x.path==='/local/fullscreen').map(x=>x.body.enabled),[true,false]);
});
await check('late native request events cannot override the current immersive intent',async()=>{
  const ui=fixture({native:true});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Fullscreen');ui.document.fire('deskwing-fullscreen',{detail:{enabled:false,reason:'request'}});assert.equal(ui.host.classList.contains('pen-tablet-fullscreen'),true);ui.context.DeskPenTablet.setActive(false);await drain();
});
await check('polling waits for pending begin and raw begin preserves late reply cleanup',async()=>{
  const held=deferred(),ui=fixture({handle:(_path,body)=>body?.operation==='begin'?held.promise:undefined});ui.context.DeskPenTablet.setActive(true);await drain();await ui.timer.advance(1900);await ui.click('Start pen session');assert.equal(ui.calls.find(x=>x.body?.operation==='begin').timeout,0);await ui.timer.advance(120);assert.equal(ui.calls.filter(x=>x.body===undefined).length,1);assert.equal(ui.find('Refresh').disabled,true);held.resolve({lease:'fixture-lease',sequence:0,maxBatch:32});await drain();assert.equal(ui.context.DeskPenTablet.isBusy(),true);await ui.timer.advance(250);assert.equal(ui.context.DeskPenTablet.isBusy(),true);ui.context.DeskPenTablet.setActive(false);await drain();
});
await check('blur, pagehide and hidden document immediately end active input',async()=>{
  for(const type of ['blur','pagehide','visibilitychange']){const ui=fixture();ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Start pen session');if(type==='visibilitychange'){ui.document.hidden=true;ui.document.fire(type);}else ui.windowFire(type);await drain();assert.equal(ui.context.DeskPenTablet.isBusy(),false);assert.ok(ui.calls.some(x=>x.body?.operation==='cancel'));}
});
console.log(`\n${passed} pen tablet checks passed; fake transport only.`);
}

function event(pointerType,extra={}){return {pointerType,pointerId:7,button:0,buttons:1,pressure:.5,tiltX:0,tiltY:0,clientX:100,clientY:100,preventDefault(){},...extra};}
function fixture(options={}){
  const timer=clock(),nodes=[],calls=[],windowEvents=new Map();let remoteActive=false;
  class Node {
    constructor(tag){this.tagName=tag;this.children=[];this.style={};this.events=new Map();this.attributes={};this.hidden=false;this.disabled=false;this.textContent='';this.value='';this.capture=new Set();const classes=new Set();this.classList={add:(...xs)=>xs.forEach(x=>classes.add(x)),remove:(...xs)=>xs.forEach(x=>classes.delete(x)),toggle:(x,value)=>{const add=value===undefined?!classes.has(x):value;add?classes.add(x):classes.delete(x);return add;},contains:x=>classes.has(x)};nodes.push(this);}
    append(...children){this.children.push(...children);}replaceChildren(...children){this.children=[...children];}setAttribute(k,v){this.attributes[k]=v;}addEventListener(type,fn){if(!this.events.has(type))this.events.set(type,[]);this.events.get(type).push(fn);}fire(type,arg={}){for(const fn of this.events.get(type)||[])fn(arg);}get clientWidth(){return Number.parseFloat(this.style.width)||options.width||800;}get clientHeight(){return Number.parseFloat(this.style.height)||options.height||500;}getBoundingClientRect(){return {left:0,top:0,width:this.clientWidth,height:this.clientHeight};}setPointerCapture(id){this.capture.add(id);}hasPointerCapture(id){return this.capture.has(id);}releasePointerCapture(id){this.capture.delete(id);this.fire('lostpointercapture',event('pen',{pointerId:id}));}
  }
  const host=new Node('section'),document=new Node('document');document.readyState='complete';document.hidden=false;document.body=new Node('body');document.createElement=tag=>new Node(tag);document.getElementById=id=>id==='pen-tablet-workspace'?host:null;
  const status=()=>({enabled:true,supported:true,active:remoteActive,error:null,target:{id:'monitor-1',label:'Test screen',kind:'monitor',width:1600,height:1000}});
  const context={document,console,performance:{now:timer.now},setTimeout:timer.schedule,clearTimeout:timer.unschedule,Uint8Array,crypto:webcrypto,desktopConsole:false,addEventListener(type,fn){if(!windowEvents.has(type))windowEvents.set(type,[]);windowEvents.get(type).push(fn);},api:async(path,body,timeout)=>{calls.push({path,body:body?structuredClone(body):undefined,timeout});if(options.handle){const override=options.handle(path,body);if(override!==undefined){const result=await override;if(body?.operation==='begin'&&result?.lease)remoteActive=true;if(['end','cancel'].includes(body?.operation))remoteActive=false;return result;}}if(!body)return status();if(body.operation==='begin'){remoteActive=true;return {lease:'fixture-lease',sequence:0,maxBatch:32};}if(['end','cancel'].includes(body.operation))remoteActive=false;return {ok:true,sequence:body.sequence};}};
  context.native=options.native===true;context.nativeRequest=async(path,body,method,timeout)=>{calls.push({path,body:structuredClone(body),method,timeout});if(options.nativeHandle){const result=options.nativeHandle(path,body);if(result!==undefined)return result;}return {enabled:body.enabled,supported:true};};
  context.window=context;context.globalThis=context;vm.createContext(context);vm.runInContext(fs.readFileSync(source,'utf8'),context,{filename:source});
  return {context,document,host,calls,timer,find:text=>nodes.find(node=>node.tagName==='button'&&node.textContent===text),byClass:name=>nodes.find(node=>node.className===name),byAttribute:(name,value)=>nodes.find(node=>node.attributes[name]===value),pad:()=>nodes.find(node=>node.className==='pen-tablet-pad'),async click(text){const node=this.find(text);assert.ok(node,`button ${text}`);assert.equal(node.disabled,false,`${text} enabled`);node.fire('click');await drain();},windowFire(type){for(const fn of windowEvents.get(type)||[])fn();}};
}
main().catch(error=>{console.error(error);process.exitCode=1;});
