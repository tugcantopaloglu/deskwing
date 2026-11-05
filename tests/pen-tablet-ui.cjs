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
const previewTarget={id:'monitor-1',width:1600,height:1000};
function goodFrame(sequence=1,target=previewTarget){return {image:'data:image/jpeg;base64,/9j/2Q==',width:640,height:400,targetId:target.id,targetWidth:target.width,targetHeight:target.height,sequence,capturedAt:'2026-11-06T12:00:00.000Z'};}
function previewHarness(options={}){
  const timer=clock(),calls=[],states=[];let displayed=null,cleared=0,frameSequence=0;
  const preview=core.createPreview({...timer,width:()=>640,request:async(body,timeout)=>{calls.push({body,timeout,at:timer.now()});if(options.request){const value=options.request(body,calls);if(value!==undefined)return value;}return goodFrame(++frameSequence);},onClear:()=>{displayed=null;cleared++;},onState:state=>states.push(state),onFrame:async(frame,current)=>{if(options.decode)await options.decode(frame,current);if(current())displayed=frame;}});
  return {timer,calls,states,preview,get displayed(){return displayed;},get cleared(){return cleared;}};
}
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
  const packets=h.calls.filter(x=>x.operation==='samples');assert.deepEqual(packets.flatMap(x=>x.samples.map(s=>s.phase)),['down','move','up']);assert.equal(packets[0].sequence,1);assert.equal(packets[0].elapsedMs,0);assert.equal(packets[1].sequence,2);assert.equal(h.client.snapshot().inFlight,false);await h.client.stop();
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
  const h=harness();await begin(h.client);for(let i=0;i<core.constants.MAX_PENDING+3;i++)h.client.enqueue(point(i%2?'up':'down'));await drain();assert.equal(h.client.active,false);assert.equal(h.client.snapshot().pending,0);assert.ok(h.calls.some(x=>x.operation==='cancel'));
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
await check('first contact is immediate and fast acknowledgements remain capped at125Hz',async()=>{
  const h=harness();await begin(h.client);h.client.enqueue(point('down'));await drain();assert.equal(h.calls.filter(x=>x.operation==='samples').length,1);assert.equal(h.calls.find(x=>x.operation==='samples').elapsedMs,0);for(let i=0;i<90;i++)h.client.enqueue(point('move',i/100));await h.timer.advance(7);assert.equal(h.calls.filter(x=>x.operation==='samples').length,1);await h.timer.advance(1);assert.equal(h.calls.filter(x=>x.operation==='samples').length,2);await h.timer.advance(16);const packets=h.calls.filter(x=>x.operation==='samples');assert.equal(packets.length,4);assert.ok(packets.slice(1).every((packet,index)=>packet.elapsedMs-packets[index].elapsedMs>=8));await h.client.stop();
});
await check('backpressured motion flushes on acknowledgement without another frame delay',async()=>{
  const held=deferred();let first=true;const h=harness(packet=>{if(packet.operation==='samples'&&first){first=false;return held.promise;}});await begin(h.client);h.client.enqueue(point('down'));await drain();h.client.enqueue(point('move',.8));await h.timer.advance(20);assert.equal(h.calls.filter(x=>x.operation==='samples').length,1);held.resolve({ok:true,sequence:1});await drain();await h.timer.advance(0);const packets=h.calls.filter(x=>x.operation==='samples');assert.equal(packets.length,2);assert.equal(packets[1].elapsedMs,20);await h.client.stop();
});
await check('pen-up flushes immediately when the bounded send slot is available',async()=>{
  const h=harness();await begin(h.client);h.client.enqueue(point('down'));await drain();await h.timer.advance(30);h.client.enqueue(point('up'));await drain();const packets=h.calls.filter(x=>x.operation==='samples');assert.equal(packets.length,2);assert.equal(packets[1].elapsedMs,30);assert.equal(packets[1].samples[0].phase,'up');await h.client.stop();
});
await check('lease geometry is delivered while input is still inactive',async()=>{
  const timer=clock(),target={id:'window-1',label:'Resized window',kind:'window',width:900,height:700};let applied=false,client;
  client=core.createClient({...timer,request:async packet=>packet.operation==='begin'?{lease:'geometry-lease',sequence:0,maxBatch:32,target}:{ok:true,sequence:packet.sequence},onLease:value=>{assert.equal(client.active,false);assert.equal(client.snapshot().state,'starting');assert.equal(value,target);applied=true;}});
  assert.equal(await begin(client),true);assert.equal(applied,true);assert.equal(client.active,true);await client.stop();
});
await check('preview validates JPEG, size, target geometry and fresh metadata',()=>{
  assert.equal(core.normalizeFrame(goodFrame(),previewTarget,640).sequence,1);
  for(const change of [{targetId:'other-window'},{targetWidth:1599},{targetHeight:999},{width:0},{height:1601},{width:641},{height:300},{sequence:0},{sequence:NaN},{capturedAt:'invalid'},{image:'https://example.invalid/frame.jpg'},{image:'data:image/png;base64,/9j/2Q=='},{image:'data:image/jpeg;base64,<script>'},{image:'data:image/jpeg;base64,'+'A'.repeat(core.constants.MAX_FRAME_BYTES)}])assert.throws(()=>core.normalizeFrame({...goodFrame(),...change},previewTarget,640));
  assert.throws(()=>core.normalizeFrame(goodFrame(4),previewTarget,640,4));
});
await check('preview never captures before explicit lease start and stays below12fps',async()=>{
  const h=previewHarness();await h.timer.advance(1000);assert.equal(h.calls.length,0);h.preview.start('lease-a',previewTarget);await h.timer.advance(0);assert.equal(h.calls.length,1);assert.equal(h.calls[0].timeout,5000);await h.timer.advance(1000);assert.equal(h.calls.length,11);assert.ok(h.calls.slice(1).every((call,index)=>call.at-h.calls[index].at>=96));assert.ok(h.displayed);h.preview.stop();assert.equal(h.displayed,null);await h.timer.advance(1000);assert.equal(h.calls.length,11);
});
await check('slow preview adapts cadence without queuing another frame',async()=>{
  const held=deferred();let first=true;const h=previewHarness({request:()=>{if(first){first=false;return held.promise;}}});h.preview.start('lease-a',previewTarget);await h.timer.advance(0);await h.timer.advance(200);assert.equal(h.calls.length,1);held.resolve(goodFrame());await drain();await h.timer.advance(49);assert.equal(h.calls.length,1);await h.timer.advance(1);assert.equal(h.calls.length,2);assert.equal(h.calls[1].at,250);h.preview.stop();
});
await check('timed-out raw frame retains its slot across stop and a new lease',async()=>{
  const held=deferred();let first=true;const h=previewHarness({request:()=>{if(first){first=false;return held.promise;}}});h.preview.start('lease-a',previewTarget);await h.timer.advance(0);await h.timer.advance(1500);assert.equal(h.preview.snapshot().state,'stale');assert.equal(h.preview.snapshot().inFlight,true);assert.equal(h.calls.length,1);h.preview.stop();h.preview.start('lease-b',previewTarget);await h.timer.advance(1000);assert.equal(h.calls.length,1);held.resolve(goodFrame());await drain();assert.equal(h.displayed,null);await h.timer.advance(0);assert.equal(h.calls.length,2);assert.equal(h.calls[1].body.lease,'lease-b');h.preview.stop();
});
await check('stale asynchronous decode cannot restore a stopped frame',async()=>{
  const decoding=deferred();const h=previewHarness({decode:()=>decoding.promise});h.preview.start('lease-a',previewTarget);await h.timer.advance(0);assert.equal(h.preview.snapshot().inFlight,true);h.preview.stop();decoding.resolve();await drain();assert.equal(h.displayed,null);assert.equal(h.preview.snapshot().state,'idle');assert.equal(h.calls.length,1);
});
await check('same-lease renders do not restart preview and changed target clears the old image',async()=>{
  const h=previewHarness();h.preview.start('lease-a',previewTarget);await h.timer.advance(0);const first=h.displayed;h.preview.start('lease-a',previewTarget);await h.timer.advance(0);assert.equal(h.calls.length,1);assert.equal(h.displayed,first);h.preview.start('lease-b',{id:'other-window',width:800,height:500});assert.equal(h.displayed,null);await h.timer.advance(0);assert.equal(h.displayed,null);assert.equal(h.preview.snapshot().state,'error');h.preview.stop();
});
await check('preview pacing tolerates1ms network jitter around the server84ms limit',async()=>{
  let serverAt=-Infinity,accepted=0,rejected=0,h;h=previewHarness({request:(_body,calls)=>new Promise((resolve,reject)=>h.timer.schedule(()=>{if(h.timer.now()-serverAt<84){rejected++;reject(new Error('PC preview is rate limited. It will retry shortly.'));return;}serverAt=h.timer.now();resolve(goodFrame(++accepted));},calls.length%2?10:9))});h.preview.start('lease-a',previewTarget);await h.timer.advance(3000);assert.equal(rejected,0);assert.ok(accepted>=30);assert.equal(h.cleared,1);h.preview.stop();
});
await check('explicit busy retries retain the fresh same-session frame without opening tools',async()=>{
  const h=previewHarness({request:(_body,calls)=>calls.length===2?Promise.reject(new Error('PC preview capture is busy. It will retry without queuing frames.')):goodFrame(calls.length)});h.preview.start('lease-a',previewTarget);await h.timer.advance(0);const first=h.displayed;await h.timer.advance(96);assert.equal(h.displayed,first);assert.equal(h.cleared,1);assert.equal(h.preview.snapshot().state,'retrying');assert.equal(h.states.some(state=>state.revealTools),false);await h.timer.advance(120);assert.equal(h.displayed.sequence,3);h.preview.stop();
});
await check('repeated busy replies never extend the1500ms freshness budget',async()=>{
  const h=previewHarness({request:(_body,calls)=>calls.length===1?goodFrame():Promise.reject(new Error('PC preview is rate limited. It will retry shortly.'))});h.preview.start('lease-a',previewTarget);await h.timer.advance(0);await h.timer.advance(1499);assert.ok(h.displayed);await h.timer.advance(1);assert.equal(h.displayed,null);assert.equal(h.preview.snapshot().state,'stale');assert.equal(h.states.some(state=>state.revealTools),false);h.preview.stop();
});
await check('frame freshness begins at request start rather than delayed delivery',async()=>{
  const first=deferred(),next=deferred();const h=previewHarness({request:(_body,calls)=>calls.length===1?first.promise:next.promise});h.preview.start('lease-a',previewTarget);await h.timer.advance(1400);first.resolve(goodFrame());await drain();assert.ok(h.displayed);await h.timer.advance(99);assert.ok(h.displayed);await h.timer.advance(1);assert.equal(h.displayed,null);h.preview.stop();next.resolve(goodFrame(2));await drain();
});
await check('expiry of the previous image permits a newer in-progress decode to finish',async()=>{
  const decoder=deferred(),pending=deferred();const h=previewHarness({request:(_body,calls)=>calls.length>2?pending.promise:goodFrame(calls.length),decode:frame=>frame.sequence===2?decoder.promise:undefined});h.preview.start('lease-a',previewTarget);await h.timer.advance(1500);assert.equal(h.displayed,null);assert.equal(h.preview.snapshot().inFlight,true);await h.timer.advance(1);decoder.resolve();await drain();assert.equal(h.displayed.sequence,2);assert.equal(h.preview.snapshot().state,'live');h.preview.stop();pending.resolve(goodFrame(3));await drain();
});
await check('authorization, unknown errors and invalid target metadata still clear fresh images immediately',async()=>{
  for(const failure of [new Error('The pen session has ended.'),new Error('Unknown transport error'),{...goodFrame(2),targetWidth:1599}]){const h=previewHarness({request:(_body,calls)=>calls.length===1?goodFrame():failure instanceof Error?Promise.reject(failure):failure});h.preview.start('lease-a',previewTarget);await h.timer.advance(0);assert.ok(h.displayed);await h.timer.advance(96);assert.equal(h.displayed,null);assert.equal(h.preview.snapshot().state,'error');assert.equal(h.states.at(-1).revealTools,true);h.preview.stop();}
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
await check('duplicate outer coalesced samples are not transmitted twice',async()=>{
  const ui=fixture();ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Start pen session');ui.pad().fire('pointerdown',event('pen'));const same={clientX:250,clientY:200,pressure:.6,tiltX:12,tiltY:15,buttons:1,button:-1};ui.pad().fire('pointermove',event('pen',{...same,getCoalescedEvents:()=>[same]}));await ui.timer.advance(8);const samples=ui.calls.filter(x=>x.body?.operation==='samples').flatMap(x=>x.body.samples);assert.deepEqual(samples.map(x=>x.phase),['down','move']);ui.context.DeskPenTablet.setActive(false);await drain();
});
await check('resized begin target maps both edges before input and stays stable through status polling',async()=>{
  const resized={id:'window-1',label:'Resized square window',kind:'window',width:1000,height:1000};const ui=fixture({width:800,height:600,handle:(_path,body)=>body?.operation==='begin'?{lease:'resized-lease',sequence:0,maxBatch:32,target:resized}:undefined});ui.context.DeskPenTablet.setActive(true);await drain();assert.equal(ui.pad().clientHeight,500);await ui.click('Start pen session');assert.equal(ui.context.DeskPenTablet.isBusy(),true);assert.equal(ui.pad().clientWidth,600);assert.equal(ui.pad().clientHeight,600);await ui.timer.advance(2000);assert.equal(ui.context.DeskPenTablet.isBusy(),true);assert.equal(ui.calls.filter(x=>x.body?.operation==='cancel').length,0);ui.pad().fire('pointerdown',event('pen',{clientX:0,clientY:0}));ui.pad().fire('pointermove',event('pen',{clientX:600,clientY:600}));await ui.timer.advance(8);const samples=ui.calls.filter(x=>x.body?.operation==='samples').flatMap(x=>x.body.samples);assert.equal(samples[0].x,0);assert.equal(samples[0].y,0);assert.equal(samples.at(-1).x,1);assert.equal(samples.at(-1).y,1);ui.context.DeskPenTablet.setActive(false);await drain();
});
await check('missing begin geometry fails closed and releases the returned lease',async()=>{
  const ui=fixture({handle:(_path,body)=>body?.operation==='begin'?{lease:'invalid-target-lease',sequence:0,maxBatch:32,target:null}:undefined});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Start pen session');assert.equal(ui.context.DeskPenTablet.isBusy(),false);assert.ok(ui.calls.some(x=>x.body?.operation==='cancel'&&x.body.lease==='invalid-target-lease'));assert.equal(ui.calls.filter(x=>x.body?.operation==='samples').length,0);
});
await check('live PC frame stays behind pen and palm input and clears synchronously on Stop',async()=>{
  const ui=fixture();ui.context.DeskPenTablet.setActive(true);await drain();assert.equal(ui.calls.filter(x=>x.path==='/api/pen/frame').length,0);await ui.click('Start pen session');await ui.timer.advance(0);const image=ui.byClass('pen-tablet-preview');assert.equal(image.hidden,false);assert.match(image.src,/^data:image\/jpeg;base64,/);ui.pad().fire('pointerdown',event('pen'));ui.pad().fire('pointermove',event('pen',{clientX:200}));ui.pad().fire('pointerdown',event('touch'));assert.equal(image.hidden,false);ui.find('Stop pen').fire('click');assert.equal(image.hidden,true);assert.equal(image.src,'');await drain();
});
await check('pending preview does not block8ms pen packets and late response stays hidden after exit',async()=>{
  const held=deferred(),ui=fixture({handle:(path)=>path==='/api/pen/frame'?held.promise:undefined});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Start pen session');await ui.timer.advance(0);ui.pad().fire('pointerdown',event('pen'));await drain();ui.pad().fire('pointermove',event('pen',{clientX:200}));await ui.timer.advance(8);assert.equal(ui.calls.filter(x=>x.body?.operation==='samples').length,2);assert.equal(ui.calls.filter(x=>x.path==='/api/pen/frame').length,1);ui.context.DeskPenTablet.setActive(false);held.resolve(goodFrame());await drain();assert.equal(ui.byClass('pen-tablet-preview').hidden,true);assert.equal(ui.byClass('pen-tablet-preview').src,'');
});
await check('preview decode finishing after background cannot expose the old target',async()=>{
  const ui=fixture({holdDecode:true});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Start pen session');await ui.timer.advance(0);ui.context.penTabletSuspend();ui.finishDecodes();await drain();assert.equal(ui.byClass('pen-tablet-preview').hidden,true);assert.equal(ui.byClass('pen-tablet-preview').src,'');
});
await check('preview error exposes app-view recovery while leaving pen input responsive',async()=>{
  const ui=fixture({handle:path=>{if(path==='/api/pen/frame')throw new Error('This app does not support capture.');}});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Fullscreen');await ui.click('Start pen');await ui.timer.advance(0);assert.equal(ui.context.DeskPenTablet.isBusy(),true);assert.ok(ui.host.classList.contains('pen-tablet-tools-open'));assert.match(ui.byClass('pen-tablet-preview-status').textContent,/does not support capture/);assert.match(ui.byClass('pen-tablet-preview-status').textContent,/choose a screen/);assert.equal(ui.byClass('pen-tablet-preview').hidden,true);ui.context.DeskPenTablet.setActive(false);await drain();
});
await check('transient capture retries keep fullscreen closed and preserve its fresh image',async()=>{
  let frames=0;const ui=fixture({handle:path=>{if(path==='/api/pen/frame'&&++frames>1)throw new Error('PC preview is rate limited. It will retry shortly.');}});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Fullscreen');await ui.click('Start pen');await ui.timer.advance(0);const image=ui.byClass('pen-tablet-preview');assert.equal(image.hidden,false);await ui.timer.advance(96);assert.equal(image.hidden,false);assert.equal(ui.host.classList.contains('pen-tablet-tools-open'),false);await ui.timer.advance(1404);assert.equal(image.hidden,true);assert.equal(ui.host.classList.contains('pen-tablet-tools-open'),false);assert.equal(ui.host.classList.contains('pen-tablet-fullscreen'),true);assert.match(ui.byClass('pen-tablet-hint').textContent,/paused/);ui.context.DeskPenTablet.setActive(false);await drain();
});
await check('a dismissed hard-error panel does not reopen on every retry',async()=>{
  const ui=fixture({handle:path=>{if(path==='/api/pen/frame')throw new Error('This app does not support capture.');}});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Fullscreen');await ui.click('Start pen');await ui.timer.advance(0);assert.equal(ui.host.classList.contains('pen-tablet-tools-open'),true);await ui.click('Hide tools');await ui.timer.advance(500);assert.equal(ui.host.classList.contains('pen-tablet-tools-open'),false);ui.context.DeskPenTablet.setActive(false);await drain();
});
await check('visible-image expiry does not cancel a newer browser image decode',async()=>{
  const ui=fixture({holdDecode:true});ui.context.DeskPenTablet.setActive(true);await drain();await ui.click('Start pen session');await ui.timer.advance(0);ui.finishDecodes();await drain();assert.equal(ui.byClass('pen-tablet-preview').hidden,false);await ui.timer.advance(1500);assert.equal(ui.byClass('pen-tablet-preview').hidden,true);ui.finishDecodes();await drain();assert.equal(ui.byClass('pen-tablet-preview').hidden,false);ui.context.DeskPenTablet.setActive(false);await drain();
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
  const timer=clock(),nodes=[],calls=[],windowEvents=new Map();let remoteActive=false,frameSequence=0,remoteTarget={id:'monitor-1',label:'Test screen',kind:'monitor',width:1600,height:1000};
  class Node {
    constructor(tag){this.tagName=tag;this.children=[];this.style={};this.events=new Map();this.attributes={};this.hidden=false;this.disabled=false;this.textContent='';this.value='';this.capture=new Set();const classes=new Set();this.classList={add:(...xs)=>xs.forEach(x=>classes.add(x)),remove:(...xs)=>xs.forEach(x=>classes.delete(x)),toggle:(x,value)=>{const add=value===undefined?!classes.has(x):value;add?classes.add(x):classes.delete(x);return add;},contains:x=>classes.has(x)};nodes.push(this);}
    append(...children){this.children.push(...children);}replaceChildren(...children){this.children=[...children];}setAttribute(k,v){this.attributes[k]=v;}removeAttribute(k){delete this.attributes[k];if(k==='src')this._src='';}set src(value){this._src=value;if(this.tagName==='img'&&this.onload&&!options.holdDecode)Promise.resolve().then(()=>this.onload?.());}get src(){return this._src||'';}get naturalWidth(){return this.width;}get naturalHeight(){return this.height;}addEventListener(type,fn){if(!this.events.has(type))this.events.set(type,[]);this.events.get(type).push(fn);}fire(type,arg={}){for(const fn of this.events.get(type)||[])fn(arg);}get clientWidth(){return Number.parseFloat(this.style.width)||options.width||800;}get clientHeight(){return Number.parseFloat(this.style.height)||options.height||500;}getBoundingClientRect(){return {left:0,top:0,width:this.clientWidth,height:this.clientHeight};}setPointerCapture(id){this.capture.add(id);}hasPointerCapture(id){return this.capture.has(id);}releasePointerCapture(id){this.capture.delete(id);this.fire('lostpointercapture',event('pen',{pointerId:id}));}
  }
  const host=new Node('section'),document=new Node('document');document.readyState='complete';document.hidden=false;document.body=new Node('body');document.createElement=tag=>new Node(tag);document.getElementById=id=>id==='pen-tablet-workspace'?host:null;
  const status=()=>({enabled:true,supported:true,active:remoteActive,error:null,target:remoteTarget});
  const context={document,console,performance:{now:timer.now},setTimeout:timer.schedule,clearTimeout:timer.unschedule,Uint8Array,crypto:webcrypto,desktopConsole:false,addEventListener(type,fn){if(!windowEvents.has(type))windowEvents.set(type,[]);windowEvents.get(type).push(fn);},api:async(path,body,timeout)=>{calls.push({path,body:body?structuredClone(body):undefined,timeout});if(options.handle){const override=options.handle(path,body);if(override!==undefined){const result=await override;if(body?.operation==='begin'&&result?.lease){remoteActive=true;if(!Object.hasOwn(result,'target'))result.target=status().target;else if(result.target)remoteTarget=result.target;}if(['end','cancel'].includes(body?.operation))remoteActive=false;return result;}}if(!body)return status();if(path==='/api/pen/frame'){const scale=Math.min(1,body.width/remoteTarget.width,1600/remoteTarget.height);return {image:'data:image/jpeg;base64,/9j/2Q==',width:Math.max(1,Math.round(remoteTarget.width*scale)),height:Math.max(1,Math.round(remoteTarget.height*scale)),targetId:remoteTarget.id,targetWidth:remoteTarget.width,targetHeight:remoteTarget.height,sequence:++frameSequence,capturedAt:'2026-11-06T12:00:00.000Z'};}if(body.operation==='begin'){remoteActive=true;return {lease:'fixture-lease',sequence:0,maxBatch:32,target:status().target};}if(['end','cancel'].includes(body.operation))remoteActive=false;return {ok:true,sequence:body.sequence};}};
  context.native=options.native===true;context.nativeRequest=async(path,body,method,timeout)=>{calls.push({path,body:structuredClone(body),method,timeout});if(options.nativeHandle){const result=options.nativeHandle(path,body);if(result!==undefined)return result;}return {enabled:body.enabled,supported:true};};
  context.window=context;context.globalThis=context;vm.createContext(context);vm.runInContext(fs.readFileSync(source,'utf8'),context,{filename:source});
  return {context,document,host,calls,timer,find:text=>nodes.find(node=>node.tagName==='button'&&node.textContent===text),byClass:name=>nodes.find(node=>node.className===name),byAttribute:(name,value)=>nodes.find(node=>node.attributes[name]===value),pad:()=>nodes.find(node=>node.className==='pen-tablet-pad'),finishDecodes:()=>nodes.filter(node=>node.tagName==='img').forEach(node=>node.onload?.()),async click(text){const node=this.find(text);assert.ok(node,`button ${text}`);assert.equal(node.disabled,false,`${text} enabled`);node.fire('click');await drain();},windowFire(type){for(const fn of windowEvents.get(type)||[])fn();}};
}
main().catch(error=>{console.error(error);process.exitCode=1;});
